using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Backend.Features.Orders;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Features.Analysis;

// 업무 백엔드 AI 분석 경계. Oracle 스냅샷·작업 상태를 보존하고 게이트웨이 호출·예약·재시도를 조정한다.
public sealed partial class AnalysisService
{
    // 대기·재시도 작업을 제한된 개수로 선점하고 AI 게이트웨이 응답을 현재 시도 결과로 반영한다.
    public async Task<ReportingDispatchResult> DispatchAsync(DispatchBatchRequest input)
    {
        // 느린 GPU 실행 중 다음 Quartz 호출은 기다리지 않고 반환한다. 다른 업무 요청은 막지 않는다.
        if (!await coordinator.DispatchGate.WaitAsync(0))
        {
            log.LogDebug("AI 전달 배치가 이미 실행 중이므로 이번 호출을 건너뜁니다.");
            return new ReportingDispatchResult(
                0,
                0,
                0
            );
        }

        try
        {
            return await DispatchBatchCoreAsync(input);
        }
        finally
        {
            coordinator.DispatchGate.Release();
        }
    }

    private async Task<ReportingDispatchResult> DispatchBatchCoreAsync(DispatchBatchRequest input)
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "DispatchAsync",
                "입력 처리 시작"
            );
            var size = Size(input.Limit);
            var now = CollectionFormat.UtcNow();
            var ids = await db.Set<IntegrationJob>()
                .AsNoTracking()
                .Where(x => x.Operation == "AI_ANALYZE" && ((x.State == "QUEUED" && (x
                    .RetryAtUtc == null || x.RetryAtUtc <= now))
                    || (x.State == "DISPATCHED" && x.DeadlineUtc <= now)
                    || (x.Principal == "SERVICE:jobs-worker" && x.Attempt < 3
                        && ((x.State == "FAILED" && (x.Error == "ANALYSIS_TRANSPORT_FAILED"
                            || x.Error == "ANALYSIS_INPUT_UNAVAILABLE"))
                            || (x.State == "TIMED_OUT" && x
                                .Error == "ANALYSIS_DEADLINE_EXPIRED")))))
                .OrderBy(x => x.CreatedAtUtc)
                .Select(x => x.JobId)
                .Take(size)
                .ToArrayAsync();
            var completed = 0;
            var failed = 0;
            var examined = 0;
            foreach (var id in ids)
            {
                examined++;
                var expectedAttempt = await db.Set<IntegrationJob>()
                    .AsNoTracking()
                    .Where(x => x.JobId == id)
                    .Select(x => x.Attempt)
                    .SingleAsync();
                AiDispatch? dispatch;
                try
                {
                    dispatch = await ClaimAsync(id);
                }
                catch (Exception claimFailure)
                {
                    log.LogWarning(
                        "AI 작업 확보 실패: {JobId}, {FailureType}",
                        id,
                        claimFailure.GetType().Name
                    );
                    db.ChangeTracker.Clear();
                    await FinishAsync(
                        id,
                        null,
                        IsTransient(claimFailure) ? "ANALYSIS_INPUT_UNAVAILABLE" : "ANALYSIS_INPUT_CHANGED",
                        expectedAttempt,
                        requireQueued: true
                    );
                    failed++;
                    continue;
                }

                if (dispatch is null)
                {
                    log.LogDebug(
                        "{Operation}: {Decision}",
                        "DispatchAsync",
                        "분기 일치 [dispatch is null]"
                    );
                    var state = await db.Set<IntegrationJob>()
                        .AsNoTracking()
                        .Where(x => x.JobId == id)
                        .Select(x => x.State)
                        .SingleAsync();
                    if (state == "TIMED_OUT")
                    {
                        log.LogDebug(
                            "{Operation}: {Decision}",
                            "DispatchAsync",
                            "분기 일치 [state == \"TIMED_OUT\"]"
                        );
                        failed++;
                    }
                    else
                    {
                        log.LogDebug(
                            "{Operation}: {Decision}",
                            "DispatchAsync",
                            "분기 불일치 [state == \"TIMED_OUT\"]"
                        );
                    }

                    continue;
                }
                else
                {
                    log.LogDebug(
                        "{Operation}: {Decision}",
                        "DispatchAsync",
                        "분기 불일치 [dispatch is null]"
                    );
                }

                AiResultPayload? result = null;
                string? error = null;
                try
                {
                    // No Oracle row lock or transaction spans this network operation.
                    using var client = clients.CreateClient("analysis-gateway");
                    // Mock의 기존 5초 전송 제한을 유지하며 LOCAL만 실제 GPU 추론 시간을 허용한다.
                    client.Timeout = TimeSpan.FromSeconds(
                        dispatch.Configuration.Content.Settings.ProviderKind == "LOCAL" ? 128 : 5
                    );
                    var remaining = dispatch.Deadline - CollectionFormat.UtcNow();
                    using var deadline = new CancellationTokenSource(
                        remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero
                    );
                    using var request = new HttpRequestMessage(
                        HttpMethod.Post,
                        "http://integration-gateway:8080/internal/v1/ai/mock-executions"
                    );
                    request.Headers.Authorization = new AuthenticationHeaderValue(
                        "Bearer",
                        issuer.AnalysisGatewayToken()
                    );
                    request.Content = JsonContent.Create(dispatch);
                    using var response = await client.SendAsync(request, deadline.Token);
                    response.EnsureSuccessStatusCode();
                    result = await response.Content.ReadFromJsonAsync<AiResultPayload>();
                }
                catch (Exception failure)
                {
                    log.LogWarning(
                        "AI Gateway 전송 실패: {JobId}, {Attempt}, {FailureType}",
                        dispatch.JobId,
                        dispatch.Attempt,
                        failure.GetType().Name
                    );
                    error = IsTransient(failure) ? "ANALYSIS_TRANSPORT_FAILED" : "ANALYSIS_PROVIDER_REJECTED";
                }

                if (await FinishAsync(
                    id,
                    result,
                    error,
                    dispatch.Attempt
                ))
                {
                    log.LogDebug(
                        "{Operation}: {Decision}",
                        "DispatchAsync",
                        "분기 일치 [await FinishAsync( id, result, error, dispatch.Attempt )]"
                    );
                    completed++;
                }
                else
                {
                    log.LogDebug(
                        "{Operation}: {Decision}",
                        "DispatchAsync",
                        "분기 불일치 [await FinishAsync( id, result, error, dispatch.Attempt )]"
                    );
                    failed++;
                }

                db.ChangeTracker.Clear();
                if (dispatch.Configuration.Content.Settings.ProviderKind == "LOCAL")
                {
                    // 한 배치가 GPU 작업 여러 개를 직렬로 붙잡지 않도록 LOCAL은 한 건까지만 실행한다.
                    log.LogDebug("LOCAL 한 건 실행 한도에 도달해 다음 배치로 넘깁니다.");
                    break;
                }
            }

            return new ReportingDispatchResult(
                examined,
                completed,
                failed
            );
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "DispatchAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "DispatchAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 작업 행을 잠근 상태에서 만료·재시도 조건을 판단하고 이번 호출의 시도 번호와 실행 권한을 확정한다.
    private async Task<AiDispatch?> ClaimAsync(string id)
    {
        log.LogDebug(
            "{Operation}: {Decision}",
            "ClaimAsync",
            "입력 처리 시작"
        );
        await using var tx = await db.Database.BeginTransactionAsync();
        var job = (await db.Set<IntegrationJob>().FromSqlInterpolated(
            $"SELECT * FROM INTEGRATION_JOB WHERE JOB_ID = {id} FOR UPDATE SKIP LOCKED"
        ).ToListAsync()).SingleOrDefault();
        if (job is not null && job.Principal == "SERVICE:jobs-worker" && job.Attempt < 3
            && ((job.State == "FAILED" && RetryableCode(job.Error))
                || (job.State == "TIMED_OUT" && job.Error == "ANALYSIS_DEADLINE_EXPIRED")))
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ClaimAsync",
                "분기 일치 [job is not null && job.Principal == \"SERVICE:jobs-worker\" && job...]"
            );
            await ScheduleRetryAsync(job, job.Error!);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return null;
        }
        else
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ClaimAsync",
                "분기 불일치 [job is not null && job.Principal == \"SERVICE:jobs-worker\" && job...]"
            );
        }

        if (job is null || job.State is not ("QUEUED" or "DISPATCHED"))
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ClaimAsync",
                "분기 일치 [job is null || job.State is not (\"QUEUED\" or \"DISPATCHED\")]"
            );
            return null;
        }
        else
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ClaimAsync",
                "분기 불일치 [job is null || job.State is not (\"QUEUED\" or \"DISPATCHED\")]"
            );
        }

        if (job.RetryAtUtc > CollectionFormat.UtcNow())
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ClaimAsync",
                "분기 일치 [job.RetryAtUtc > CollectionFormat.UtcNow()]"
            );
            return null;
        }
        else
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ClaimAsync",
                "분기 불일치 [job.RetryAtUtc > CollectionFormat.UtcNow()]"
            );
        }

        if (job.State == "QUEUED")
        {
            var queuedIntent = await IntentAsync(job);
            if (queuedIntent.Configuration.Content.Settings.ProviderKind == "LOCAL")
            {
                // 불변 입력의 대기 시간은 GPU 실행 제한과 분리한다. 실행을 선점한 시각부터 130초다.
                job.DeadlineUtc = CollectionFormat.UtcNow()
                    .Add(ExecutionWindow(queuedIntent.Configuration.Content.Settings));
                log.LogDebug(
                    "LOCAL 실행 기한 확보: {JobId}, {DeadlineUtc}",
                    job.JobId,
                    job.DeadlineUtc
                );
            }
        }

        if (job.DeadlineUtc <= CollectionFormat.UtcNow())
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ClaimAsync",
                "분기 일치 [job.DeadlineUtc <= CollectionFormat.UtcNow()]"
            );
            if (job.Principal == "SERVICE:jobs-worker" && job.Attempt < 3)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ClaimAsync",
                    "분기 일치 [job.Principal == \"SERVICE:jobs-worker\" && job.Attempt < 3]"
                );
                await ScheduleRetryAsync(job, "ANALYSIS_DEADLINE_EXPIRED");
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return null;
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ClaimAsync",
                    "분기 불일치 [job.Principal == \"SERVICE:jobs-worker\" && job.Attempt < 3]"
                );
            }

            job.State = "TIMED_OUT";
            job.RetryAtUtc = null;
            job.Error = "ANALYSIS_DEADLINE_EXPIRED";
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return null;
        }
        else
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ClaimAsync",
                "분기 불일치 [job.DeadlineUtc <= CollectionFormat.UtcNow()]"
            );
        }

        if (job.State != "QUEUED")
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ClaimAsync",
                "분기 일치 [job.State != \"QUEUED\"]"
            );
            return null;
        }
        else
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ClaimAsync",
                "분기 불일치 [job.State != \"QUEUED\"]"
            );
        }

        if (job.SnapshotId is null && !await CaptureRetryInputAsync(job))
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ClaimAsync",
                "분기 일치 [job.SnapshotId is null && !await CaptureRetryInputAsync(job)]"
            );
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return null;
        }
        else
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ClaimAsync",
                "분기 불일치 [job.SnapshotId is null && !await CaptureRetryInputAsync(job)]"
            );
        }

        var envelope = await EnvelopeAsync(job);
        job.RetryAtUtc = null;
        job.Error = null;
        job.State = "DISPATCHED";
        await db.SaveChangesAsync();
        await tx.CommitAsync();

        return new AiDispatch(
            job.JobId,
            job.RequestId,
            job.Attempt,
            job.Operation,
            envelope.Snapshot,
            envelope.Configuration,
            job.DeadlineUtc,
            job.CorrelationId
        );
    }

    private static bool ValidResult(
        IntegrationJob job,
        AnalysisInputEnvelope envelope,
        AiResultPayload result
    )
    {
        var evidence = result.Evidence;
        var settings = envelope.Configuration.Content.Settings;
        // 게이트웨이와 모델의 결과도 신뢰하지 않고 불변 Oracle 입력으로 위험·근거를 재계산한다.
        var providerValid = LocalAiPolicy.IsAllowed(settings)
            && result.Provider == settings.ProviderKind && result.Model == settings.Model
            && (settings.ProviderKind == "MOCK"
                ? result.MockFlag && result.Risk == "UNKNOWN" && evidence?.Maintenance is null
                : !result.MockFlag && evidence?.Maintenance is not null
                    && result.Risk == MaintenanceRules.Evaluate(envelope.Input).Risk
                    && MaintenanceRules.Matches(envelope.Input, evidence.Maintenance));
        return providerValid && result.JobId == job.JobId && result.Attempt == job.Attempt
            && result.ResultVersion == 1
            && result.InputPeriodStart == envelope.Snapshot.PeriodStart
            && result.InputPeriodEnd == envelope.Snapshot.PeriodEnd
            && result.DataCoverage == envelope.Snapshot.Coverage
            && result.CompletedAt.Kind == DateTimeKind.Utc
            && result.CompletedAt >= job.CreatedAtUtc
            && result.CompletedAt <= job.DeadlineUtc
            && evidence is not null && !string.IsNullOrWhiteSpace(evidence.Summary)
            && evidence.Summary.Length <= 1000
            && evidence.InputHash == envelope.Snapshot.InputHash
            && evidence.ConfigHash == envelope.Configuration.ConfigHash
            && evidence.Observations is { Length: <= 20 }
            && evidence.Observations.All(x => x is not null && x.Length <= 500)
            && evidence.Recommendations is { Length: <= 20 }
            && evidence.Recommendations.All(x => x is not null && x.Length <= 500);
    }

    // 커넥터 응답의 작업·시도·스냅샷을 대조하고 결과·상태·알림을 함께 저장해 오래된 응답의 덮어쓰기를 막는다.
    private async Task<bool> FinishAsync(
        string id,
        AiResultPayload? payload,
        string? error,
        long? expectedAttempt = null,
        bool requireQueued = false
    )
    {
        log.LogDebug(
            "{Operation}: {Decision}",
            "FinishAsync",
            "입력 처리 시작"
        );
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync();
        var job = (await db.Set<IntegrationJob>().FromSqlInterpolated(
            $"SELECT * FROM INTEGRATION_JOB WHERE JOB_ID = {id} FOR UPDATE"
        ).ToListAsync()).Single();
        if ((expectedAttempt is not null && job.Attempt != expectedAttempt)
            || (requireQueued && job.State != "QUEUED")
            || job.State is not ("QUEUED" or "DISPATCHED"))
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "FinishAsync",
                "분기 일치 [(expectedAttempt is not null && job.Attempt != expectedAttempt) ...]"
            );
            return false;
        }
        else
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "FinishAsync",
                "분기 불일치 [(expectedAttempt is not null && job.Attempt != expectedAttempt) ...]"
            );
        }

        if (RetryableCode(error) && job.Attempt < 3)
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "FinishAsync",
                "분기 일치 [RetryableCode(error) && job.Attempt < 3]"
            );
            await ScheduleRetryAsync(job, error!);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return false;
        }
        else
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "FinishAsync",
                "분기 불일치 [RetryableCode(error) && job.Attempt < 3]"
            );
        }

        if (job.DeadlineUtc <= CollectionFormat.UtcNow())
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "FinishAsync",
                "분기 일치 [job.DeadlineUtc <= CollectionFormat.UtcNow()]"
            );
            job.State = "TIMED_OUT";
            job.RetryAtUtc = null;
            job.Error = "ANALYSIS_DEADLINE_EXPIRED";
        }
        else
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "FinishAsync",
                "분기 불일치 [job.DeadlineUtc <= CollectionFormat.UtcNow()]"
            );
            AnalysisInputEnvelope? envelope = null;
            if (error is null)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "FinishAsync",
                    "분기 일치 [error is null]"
                );
                try
                {
                    envelope = await EnvelopeAsync(job);
                }
                catch (Exception failure)
                {
                    error = IsTransient(failure) ? "ANALYSIS_INPUT_UNAVAILABLE" : "ANALYSIS_INPUT_CHANGED";
                }
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "FinishAsync",
                    "분기 불일치 [error is null]"
                );
            }

            if (RetryableCode(error) && job.Attempt < 3)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "FinishAsync",
                    "분기 일치 [RetryableCode(error) && job.Attempt < 3]"
                );
                await ScheduleRetryAsync(job, error!);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return false;
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "FinishAsync",
                    "분기 불일치 [RetryableCode(error) && job.Attempt < 3]"
                );
            }

            if (error is not null || payload is null || envelope is null
                || !ValidResult(
                    job,
                    envelope,
                    payload
                ))
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "FinishAsync",
                    "분기 일치 [error is not null || payload is null || envelope is null || !Val...]"
                );
                job.State = "FAILED";
                job.Error = error ?? "ANALYSIS_RESULT_REJECTED";
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "FinishAsync",
                    "분기 불일치 [error is not null || payload is null || envelope is null || !Val...]"
                );
                var result = new AiAnalysisResult
                {
                    JobId = job.JobId,
                    Attempt = job.Attempt,
                    Provider = payload.Provider,
                    Model = payload.Model,
                    InputPeriodStartUtc = payload.InputPeriodStart,
                    InputPeriodEndUtc = payload.InputPeriodEnd,
                    DataCoverage = decimal.Parse(payload.DataCoverage,
                        System.Globalization.CultureInfo.InvariantCulture),
                    ResultVersion = 1,
                    Risk = payload.Risk,
                    Evidence = JsonSerializer.Serialize(payload.Evidence, Json),
                    MockFlag = payload.MockFlag,
                    Accepted = false,
                    CompletedAtUtc = payload.CompletedAt
                };
                db.Add(result);
                job.State = "SUCCEEDED";
                job.Error = null;
                await db.SaveChangesAsync();
                // 결과·알림은 같은 트랜잭션으로 저장한다. 로컬 분석도 자동 정비나 설비 제어를 실행하지 않는다.
                // 수동 분석: 요청자(AI_COMPLETED). 예약 분석: 위험도 MEDIUM·HIGH일 때만, AI_SCHEDULED에서
                // 채널을 하나 이상 켠 활성 관리자(AI_SCHEDULED, OFF/OFF는 구독 해제).
                // 알림 원인 ID는 결과 ID로 정해 같은 결과로 알림이 두 번 생기지 않게 한다.
                var scheduled = job.ActorId is null;
                long[] recipients = job.ActorId is { } actor
                    ? [actor]
                    : payload.Risk is "MEDIUM" or "HIGH"
                        ? await ScheduledRecipientsAsync()
                        : [];
                log.LogDebug(
                    "분석 완료 알림 대상: {JobId}, {Scheduled}, {Risk}, {RecipientCount}",
                    job.JobId,
                    scheduled,
                    payload.Risk,
                    recipients.Length
                );
                if (recipients.Length > 0)
                {
                    await inbox.CreateAsync(
                        "analysis-result:" + result.ResultId.ToString(
                            System.Globalization.CultureInfo.InvariantCulture
                        ),
                        payload.MockFlag ? "Mock AI 분석 완료" : "로컬 AI 유지보전 검토 완료",
                        payload.MockFlag
                            ? "모의 분석 참고 결과입니다. 실제 예측 또는 유지보전 경보가 아닙니다."
                            : $"시연 기준 상태 {payload.Risk}. "
                                + "AI 설명은 참고 자료이며 현장 담당자의 검토가 필요합니다.",
                        null,
                        recipients,
                        job.CorrelationId,
                        eventKind: scheduled ? "AI_SCHEDULED" : "AI_COMPLETED",
                        analysisResultId: result.ResultId
                    );
                }
            }
        }

        job.RetryAtUtc = null;
        db.Add(new AuditEvent
        {
            ActorId = job.ActorId,
            Principal = "SERVICE:jobs-worker",
            Action = "analysis.completed",
            EntityType = "INTEGRATION_JOB",
            EntityId = job.JobId,
            Detail = CollectionFormat.Canonical(new
            {
                job.Attempt,
                job.State,
                job.Error,
                accepted = false
            }),
            OccurredAtUtc = CollectionFormat.UtcNow()
        });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        if (job.State == "SUCCEEDED")
        {
            log.LogInformation(
                "AI 분석 완료: {JobId}, {Attempt}, {SnapshotId}, {Provider}, {Risk}",
                job.JobId,
                job.Attempt,
                job.SnapshotId,
                payload?.Provider,
                payload?.Risk
            );
        }
        else
        {
            log.LogWarning(
                "AI 분석 종료: {JobId}, {Attempt}, {State}, {FailureCode}",
                job.JobId,
                job.Attempt,
                job.State,
                job.Error
            );
        }
        return job.State == "SUCCEEDED";
    }

    // 예약 분석 알림 수신자: 활성 ADMIN 가운데 AI_SCHEDULED 채널을 하나 이상 켠 사용자.
    // 설정 행이 없거나 OFF/OFF면 구독하지 않은 것으로 본다(Inbox 전용 구독은 이번 범위 밖).
    private async Task<long[]> ScheduledRecipientsAsync() =>
        await db.Set<NotificationChannelSetting>().AsNoTracking()
            .Where(x => x.EventKind == "AI_SCHEDULED"
                && (x.EmailMode != "OFF" || x.ChatMode != "OFF")
                && db.Set<UserAccount>().Any(u => u.UserId == x.UserId
                    && u.Active == EF.Parameter(true))
                && db.Set<UserRole>().Any(r => r.UserId == x.UserId && r.Role == "ADMIN"))
            .OrderBy(x => x.UserId)
            .Select(x => x.UserId)
            .ToArrayAsync();
}







