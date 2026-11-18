using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using FactoryMES.Backend.Features.Orders;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Features.Analysis;

// 업무 백엔드 AI 분석 경계. Oracle 스냅샷·작업 상태를 보존하고 게이트웨이 호출·예약·재시도를 조정한다.
public sealed partial class AnalysisService
{
    private static AnalysisRequest Normalize(AnalysisRequest input)
    {
        Check(Guid.TryParseExact(
            input.RequestId,
            "D",
            out _
        ), "요청 ID는 UUID입니다.");
        Check(input.PeriodStart.Kind == DateTimeKind.Utc && input.PeriodEnd.Kind == DateTimeKind.Utc
            && input.PeriodStart.Ticks % 10 == 0 && input.PeriodEnd.Ticks % 10 == 0
            && input.PeriodStart >= new DateTime(
                2000,
                1,
                1,
                0,
                0,
                0,
                DateTimeKind.Utc
            )
            && input.PeriodEnd <= CollectionFormat.UtcNow()
            && input.PeriodEnd - input.PeriodStart >= TimeSpan.FromMinutes(1)
            && input.PeriodEnd - input.PeriodStart <= TimeSpan.FromDays(1),
            "UTC 과거 시각의 1분~24시간 기간을 선택하세요.");
        Check(input.AnalysisKind is "MAINTENANCE_RISK" or "TREND_SUMMARY", "분석 종류를 확인하세요.");
        OrderService.ParseId(input.EquipmentId);

        return input with
        {
            RequestId = Guid.Parse(input.RequestId).ToString()
        };
    }

    // 요청 시간대의 Oracle 수집 집계·작업 중지·경보 정보를 읽어 AI에 전달할 고정 입력을 구성한다.
    private async Task<AnalysisInput> BuildInputAsync(
        AnalysisRequest request,
        bool includeObservation = false
    )
    {
        log.LogDebug(
            "{Operation}: {Decision}",
            "BuildInputAsync",
            "입력 처리 시작"
        );
        var equipment = OrderService.ParseId(request.EquipmentId);
        Check(await db.Set<Equipment>().AnyAsync(x => x.EquipmentId == equipment
            && x.Active == EF.Parameter(true)), "활성 설비를 선택하세요.");
        var aggregate = await AggregateMetricsAsync(
            equipment,
            request.PeriodStart,
            request.PeriodEnd,
            includeObservation
        );
        var expected = (decimal)Math.Ceiling((request.PeriodEnd - request.PeriodStart)
            .TotalMinutes);
        var coverage = Math.Round(Math.Min(1, aggregate.Buckets / expected), 6);
        var pauses = await db.Set<WorkPause>()
            .AsNoTracking()
            .Where(x => x.EquipmentId == equipment && x.StartedAtUtc < request.PeriodEnd
                && (x.EndedAtUtc == null || x.EndedAtUtc > request.PeriodStart))
            .OrderBy(x => x.StartedAtUtc)
            .Take(10001)
            .ToArrayAsync();
        Check(pauses.Length <= 10000, "중지 이력이 너무 많습니다. 기간을 줄이세요.");

        var until = request.PeriodStart;
        double downtime = 0;
        foreach (var pause in pauses)
        {
            var start = pause.StartedAtUtc > request.PeriodStart
                ? pause.StartedAtUtc : request.PeriodStart;
            var end = pause.EndedAtUtc is { } stopped && stopped < request.PeriodEnd
                ? stopped : request.PeriodEnd;
            if (start < until)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "BuildInputAsync",
                    "분기 일치 [start < until]"
                );
                start = until;
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "BuildInputAsync",
                    "분기 불일치 [start < until]"
                );
            }

            if (end > start)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "BuildInputAsync",
                    "분기 일치 [end > start]"
                );
                downtime += (end - start).TotalSeconds;
                until = end;
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "BuildInputAsync",
                    "분기 불일치 [end > start]"
                );
            }
        }

        var alarms = await db.Set<Alarm>().LongCountAsync(x => x.EquipmentId == equipment
            && x.OpenedAtUtc >= request.PeriodStart && x.OpenedAtUtc < request.PeriodEnd);

        return new AnalysisInput(
            1,
            request.EquipmentId,
            request.PeriodStart,
            request.PeriodEnd,
            CollectionFormat.Number(coverage),
            aggregate.Samples,
            aggregate.Metrics,
            (long)Math.Floor(downtime),
            alarms
        );
    }

    // 사용자 요청을 받아 행위자 식별자를 확인하고 정규화·중복 방지·스냅샷 저장 경로로 전달한다.
    public Task<AnalysisJobView> QueueAsync(AnalysisRequest input, ClaimsPrincipal user)
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "QueueAsync",
                "입력 처리 시작"
            );
            var actor = OrderService.Actor(user);
            log.LogDebug("AI 요청자 확인: {ActorUserId}", actor);
            return QueueCoreAsync(
                input,
                actor,
                "USER:" + actor
            );
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "QueueAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "QueueAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 정규화한 요청·설정을 받아 요청 식별자 중복을 확인하고 불변 스냅샷과 대기 작업을 원자적으로 저장한다.
    private async Task<AnalysisJobView> QueueCoreAsync(
        AnalysisRequest input,
        long? actor,
        string principal
    )
    {
        log.LogDebug(
            "{Operation}: {Decision}",
            "QueueCoreAsync",
            "입력 처리 시작"
        );
        var request = Normalize(input);
        await coordinator.Gate.WaitAsync();
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            if (actor is not null)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "QueueCoreAsync",
                    "분기 일치 [actor is not null]"
                );
                await db.Set<UserAccount>().FromSqlInterpolated(
                    $"SELECT * FROM USER_ACCOUNT WHERE USER_ID = {actor.Value} FOR UPDATE"
                ).ToListAsync();
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "QueueCoreAsync",
                    "분기 불일치 [actor is not null]"
                );
            }

            var existing = await db.Set<IntegrationJob>().SingleOrDefaultAsync(x =>
                x.Principal == principal && x.RequestId == request.RequestId && x
                    .Operation == "AI_ANALYZE");
            if (existing is not null)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "QueueCoreAsync",
                    "분기 일치 [existing is not null]"
                );
                var intent = await IntentAsync(existing);
                if (CollectionFormat.Canonical(intent.Request) != CollectionFormat
                    .Canonical(request))
                {
                    log.LogDebug(
                        "{Operation}: {Decision}",
                        "QueueCoreAsync",
                        "분기 일치 [CollectionFormat.Canonical(intent.Request) != CollectionFormat ....]"
                    );
                    throw new OrderFault(
                        409,
                        "AnalysisConflict",
                        "같은 요청 ID의 내용이 다릅니다."
                    );
                }
                else
                {
                    log.LogDebug(
                        "{Operation}: {Decision}",
                        "QueueCoreAsync",
                        "분기 불일치 [CollectionFormat.Canonical(intent.Request) != CollectionFormat ....]"
                    );
                }

                log.LogDebug(
                    "AI 요청 멱등 재생: {JobId}, {Attempt}, {State}",
                    existing.JobId,
                    existing.Attempt,
                    existing.State
                );
                return await ViewAsync(existing);
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "QueueCoreAsync",
                    "분기 불일치 [existing is not null]"
                );
            }

            var targets = await db.Set<IntegrationTarget>().FromSqlInterpolated(
                $"SELECT * FROM INTEGRATION_TARGET WHERE TARGET_CODE = {request.TargetId} FOR UPDATE"
            ).ToListAsync();
            var target = targets.SingleOrDefault(x => x.Kind == "AI" && !x.Deleted);
            Check(target is not null && target.Enabled && target.ConnectorType == "ai-http",
                "활성 Mock 또는 로컬 AI 대상을 선택하세요.");
            var settings = await db.Set<AiProviderConfig>().SingleAsync(x => x.TargetId == target!
                .TargetId);
            var configuration = Compile(target!, settings);
            Check(LocalAiPolicy.IsAllowed(configuration.Content.Settings),
                "허용된 Mock 또는 로컬 AI 설정을 선택하세요.");
            log.LogDebug("AI 요청 설정 검증 완료: {Provider}", settings.ProviderKind);
            var data = await BuildInputAsync(
                request,
                settings.ProviderKind == "LOCAL"
            );
            var coverage = decimal.Parse(data.Coverage, CultureInfo.InvariantCulture);
            var snapshot = new AnalysisSnapshot
            {
                EquipmentId = OrderService.ParseId(request.EquipmentId),
                PeriodStartUtc = request.PeriodStart,
                PeriodEndUtc = request.PeriodEnd,
                InputVersion = 1,
                Coverage = coverage,
                Payload = CollectionFormat.Canonical(data),
                InputHash = CollectionFormat.Hash(data)
            };
            db.Add(snapshot);
            await db.SaveChangesAsync();
            var job = new IntegrationJob
            {
                JobId = Guid.NewGuid().ToString(),
                RequestId = request.RequestId,
                TargetId = target!.TargetId,
                Operation = "AI_ANALYZE",
                ActorId = actor,
                Principal = principal,
                SnapshotId = snapshot.SnapshotId,
                ConfigVersion = target.ConfigVersion,
                State = data.SampleCount >= 2 && coverage >= 0.1m ? "QUEUED" : "INSUFFICIENT_DATA",
                DeadlineUtc = CollectionFormat.UtcNow().Add(ExecutionWindow(configuration.Content.Settings)),
                CorrelationId = request.RequestId
            };
            db.Add(job);
            db.Add(new AuditEvent
            {
                ActorId = actor,
                Principal = principal,
                Action = "analysis.requested",
                EntityType = "INTEGRATION_JOB",
                EntityId = job.JobId,
                Detail = JsonSerializer.Serialize(new Intent(request, configuration), Json),
                OccurredAtUtc = CollectionFormat.UtcNow()
            });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            log.LogInformation(
                "AI 분석 접수 완료: {ActorUserId}, {JobId}, {State}, {SnapshotId}",
                actor,
                job.JobId,
                job.State,
                job.SnapshotId
            );
            return await ViewAsync(job);
        }
        finally
        {
            coordinator.Gate.Release();
        }
    }

    // 저장된 작업과 스냅샷을 읽어 커넥터가 소비하는 입력 봉투를 복원한다. 원문은 로그에 출력하지 않는다.
    private async Task<AnalysisInputEnvelope> EnvelopeAsync(IntegrationJob job)
    {
        log.LogDebug(
            "{Operation}: {Decision}",
            "EnvelopeAsync",
            "입력 처리 시작"
        );
        var snapshot = await db.Set<AnalysisSnapshot>()
            .AsNoTracking()
            .SingleAsync(x => x.SnapshotId == job.SnapshotId);
        var input = JsonSerializer.Deserialize<AnalysisInput>(snapshot.Payload, Json)!;
        var intent = await IntentAsync(job);
        var target = await db.Set<IntegrationTarget>()
            .AsNoTracking()
            .SingleAsync(x => x.TargetId == job.TargetId);
        var settings = await db.Set<AiProviderConfig>()
            .AsNoTracking()
            .SingleAsync(x => x.TargetId == job.TargetId);
        Check(target.Enabled && !target.Deleted && target.ConfigVersion == job.ConfigVersion
            && Compile(target, settings).ConfigHash == intent.Configuration.ConfigHash
            && CollectionFormat.Hash(input) == snapshot.InputHash,
            "분석 원본 또는 설정이 변경되었습니다.");

        return new AnalysisInputEnvelope(
            Snapshot(snapshot),
            input,
            intent.Configuration,
            intent.Request.AnalysisKind
        );
    }

    // 내부 커넥터가 요청한 작업·시도 번호를 검증해 현재 시도에 허용된 스냅샷만 반환한다.
    public async Task<AnalysisInputEnvelope> InputAsync(string id, long attempt)
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "InputAsync",
                "입력 처리 시작"
            );
            id = JobId(id);
            var job = await db.Set<IntegrationJob>()
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.JobId == id && x.Operation == "AI_ANALYZE");
            Check(job is not null && job.Attempt == attempt && job.State == "DISPATCHED"
                && job.DeadlineUtc > CollectionFormat.UtcNow(), "유효한 실행 중 분석이 아닙니다.");

            return await EnvelopeAsync(job!);
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "InputAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "InputAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }
}








