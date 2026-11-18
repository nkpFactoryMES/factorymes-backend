using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryMES.Backend.Features.Orders;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Features.Analysis;

// 업무 백엔드 AI 분석 경계. Oracle 스냅샷·작업 상태를 보존하고 게이트웨이 호출·예약·재시도를 조정한다.
public sealed partial class AnalysisService
{
    private sealed class ScheduledCandidate
    {
        public long EquipmentId { get; set; }
        public DateTime PeriodEnd { get; set; }
    }

    // 내부 예약 호출을 받아 프로세스 내 스케줄 게이트로 동시 실행을 제한한 뒤 DB 기반 복구·순환 등록을 수행한다.
    public async Task<ReportingDispatchResult> ScheduledAsync()
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ScheduledAsync",
                "입력 처리 시작"
            );
            await coordinator.ScheduleGate.WaitAsync();
            try
            {
                return await ScheduleBatchAsync();
            }
            finally
            {
                coordinator.ScheduleGate.Release();
            }
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "ScheduledAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "ScheduledAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // DB의 예약 상태와 커서를 읽어 누락 슬롯·대상 설비를 제한된 개수로 등록하고 다음 순환 위치를 보존한다.
    private async Task<ReportingDispatchResult> ScheduleBatchAsync()
    {
        log.LogDebug(
            "{Operation}: {Decision}",
            "ScheduleBatchAsync",
            "입력 처리 시작"
        );
        var local = DateTime.UtcNow.AddHours(9);
        var end = DateTime.SpecifyKind(local.Date.AddHours(2).AddHours(-9), DateTimeKind.Utc);
        if (end > CollectionFormat.UtcNow())
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ScheduleBatchAsync",
                "분기 일치 [end > CollectionFormat.UtcNow()]"
            );
            end = end.AddDays(-1);
        }
        else
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ScheduleBatchAsync",
                "분기 불일치 [end > CollectionFormat.UtcNow()]"
            );
        }

        var target = await (
            from row in db.Set<IntegrationTarget>()
            join settings in db.Set<AiProviderConfig>() on row.TargetId equals settings.TargetId
            where row.Kind == "AI" && row.Enabled == EF.Parameter(true)
                && row.Deleted == EF.Parameter(false)
                && (settings.ProviderKind == "MOCK" || settings.ProviderKind == "LOCAL")
            // 기존 Mock 예약 대상을 우선해 LOCAL 등록만으로 과거 전체 GPU 분석을 시작하지 않는다.
            orderby settings.ProviderKind == "MOCK" ? 0 : 1, row.TargetId
            select row
        ).AsNoTracking()
            .FirstOrDefaultAsync();
        if (target is null)
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ScheduleBatchAsync",
                "분기 일치 [target is null]"
            );
            return new ReportingDispatchResult(
                0,
                0,
                0
            );
        }
        else
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ScheduleBatchAsync",
                "분기 불일치 [target is null]"
            );
        }

        var selectedSettings = await db.Set<AiProviderConfig>()
            .AsNoTracking()
            .SingleAsync(x => x.TargetId == target.TargetId);
        Check(LocalAiPolicy.IsAllowed(Compile(target, selectedSettings).Content.Settings),
            "예약 AI 설정을 확인하세요.");
        var localTarget = selectedSettings.ProviderKind == "LOCAL" ? 1 : 0;
        // Mock은 보존된 전체 관측을 복구한다. LOCAL만 있으면 등록 후 시작한 완전한 일별 슬롯부터 처리한다.
        var equipment = await db.Database.SqlQuery<ScheduledCandidate>($"""
            WITH windows AS (
                SELECT DISTINCT EQUIPMENT_ID,
                    TRUNC(CAST(OCCURRED_AT_UTC AS DATE) - 17/24) + 1 + 17/24 AS PERIOD_END
                FROM TELEMETRY_EVENT WHERE OCCURRED_AT_UTC < {end}
            )
            SELECT w.EQUIPMENT_ID AS "EquipmentId",
                CAST(w.PERIOD_END AS TIMESTAMP) AS "PeriodEnd"
            FROM windows w
            WHERE ({localTarget} = 0 OR w.PERIOD_END - 1 >= {target.ActivatedAtUtc})
            AND NOT EXISTS (
                SELECT 1 FROM INTEGRATION_JOB j
                JOIN AUDIT_EVENT a ON a.ENTITY_ID = j.JOB_ID
                WHERE j.PRINCIPAL = 'SERVICE:jobs-worker'
                    AND j.OPERATION = 'AI_ANALYZE' AND j.TARGET_ID = {target.TargetId}
                    AND a.ENTITY_TYPE = 'INTEGRATION_JOB' AND a.ACTION = 'analysis.requested'
                    AND JSON_VALUE(a.DETAIL, '$.request.equipmentId'
                        RETURNING NUMBER NULL ON ERROR) = w.EQUIPMENT_ID
                    AND JSON_VALUE(a.DETAIL, '$.request.periodEnd') =
                        TO_CHAR(w.PERIOD_END, 'YYYY-MM-DD"T"HH24:MI:SS"Z"')
            )
            ORDER BY w.PERIOD_END, w.EQUIPMENT_ID
            FETCH FIRST 20 ROWS ONLY
            """).ToArrayAsync();
        var queued = 0;
        var failed = 0;
        foreach (var candidate in equipment)
        {
            var id = candidate.EquipmentId;
            var periodEnd = DateTime.SpecifyKind(candidate.PeriodEnd, DateTimeKind.Utc);
            var start = periodEnd.AddDays(-1);
            var identity = Encoding.UTF8.GetBytes(target.TargetCode + ":" + id + ":" + periodEnd
                .Ticks);
            var requestId = new Guid(SHA256.HashData(identity).AsSpan(0, 16)).ToString();
            var request = new AnalysisRequest(
                requestId,
                id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                target.TargetCode,
                start,
                periodEnd,
                "TREND_SUMMARY"
            );
            try
            {
                var job = await QueueCoreAsync(
                    request,
                    null,
                    "SERVICE:jobs-worker"
                );
                if (job.State == "FAILED")
                {
                    log.LogDebug(
                        "{Operation}: {Decision}",
                        "ScheduleBatchAsync",
                        "분기 일치 [job.State == \"FAILED\"]"
                    );
                    failed++;
                }
                else
                {
                    log.LogDebug(
                        "{Operation}: {Decision}",
                        "ScheduleBatchAsync",
                        "분기 불일치 [job.State == \"FAILED\"]"
                    );
                    queued++;
                }
            }
            catch (Exception error)
            {
                log.LogWarning(
                    "예약 AI 작업 등록 실패: {TargetId}, {FailureType}",
                    target.TargetId,
                    error.GetType().Name
                );
                db.ChangeTracker.Clear();
                await RecordScheduledFailureAsync(
                    request,
                    target.TargetId,
                    error
                );
                failed++;
            }
        }

        if (queued > 0 || failed > 0)
        {
            log.LogInformation(
                "예약 AI 순환 처리 완료: {EquipmentCount}, {QueuedCount}, {FailedCount}",
                equipment.Length,
                queued,
                failed
            );
        }
        return new ReportingDispatchResult(
            equipment.Length,
            queued,
            failed
        );
    }
    // 예약 등록 실패를 별도 DB 작업 이력으로 남겨 다음 복구에서 중복 등록 여부를 판단할 수 있게 한다.
    private async Task RecordScheduledFailureAsync(
        AnalysisRequest request,
        long targetId,
        Exception error
    )
    {
        log.LogDebug(
            "{Operation}: {Decision}",
            "RecordScheduledFailureAsync",
            "입력 처리 시작"
        );
        await coordinator.Gate.WaitAsync();
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var target = (await db.Set<IntegrationTarget>().FromSqlInterpolated(
                $"SELECT * FROM INTEGRATION_TARGET WHERE TARGET_ID = {targetId} FOR UPDATE"
            ).ToListAsync()).Single();
            if (await db.Set<IntegrationJob>().AnyAsync(x => x.Principal == "SERVICE:jobs-worker"
                && x.RequestId == request.RequestId && x.Operation == "AI_ANALYZE"))
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "RecordScheduledFailureAsync",
                    "분기 일치 [await db.Set<IntegrationJob>().AnyAsync(x => x.Principal == \"SER...]"
                );
                return;
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "RecordScheduledFailureAsync",
                    "분기 불일치 [await db.Set<IntegrationJob>().AnyAsync(x => x.Principal == \"SER...]"
                );
            }

            var settings = await db.Set<AiProviderConfig>().SingleAsync(x => x
                .TargetId == targetId);
            var transient = IsTransient(error);
            var retryAt = CollectionFormat.UtcNow().AddSeconds(5);
            var code = error is OrderFault { Code: "AnalysisInputLimit" }
                ? "ANALYSIS_INPUT_LIMIT" : "ANALYSIS_SCHEDULE_REJECTED";
            var job = new IntegrationJob
            {
                JobId = Guid.NewGuid().ToString(),
                RequestId = request.RequestId,
                TargetId = targetId,
                Operation = "AI_ANALYZE",
                Principal = "SERVICE:jobs-worker",
                ConfigVersion = target.ConfigVersion,
                State = transient ? "QUEUED" : "FAILED",
                Attempt = transient ? 2 : 1,
                RetryAtUtc = transient ? retryAt : null,
                DeadlineUtc = (transient ? retryAt : CollectionFormat.UtcNow())
                    .Add(ExecutionWindow(Compile(target, settings).Content.Settings)),
                Error = transient ? "ANALYSIS_INPUT_UNAVAILABLE" : code,
                CorrelationId = request.RequestId
            };
            db.Add(job);
            db.Add(new AuditEvent
            {
                Principal = "SERVICE:jobs-worker",
                Action = "analysis.requested",
                EntityType = "INTEGRATION_JOB",
                EntityId = job.JobId,
                Detail = JsonSerializer.Serialize(new Intent(request, Compile(target, settings)),
                    Json),
                OccurredAtUtc = CollectionFormat.UtcNow()
            });
            db.Add(new AuditEvent
            {
                Principal = "SERVICE:jobs-worker",
                Action = transient ? "analysis.retry.scheduled" : "analysis.scheduled.rejected",
                EntityType = "INTEGRATION_JOB",
                EntityId = job.JobId,
                Detail = CollectionFormat.Canonical(new
                {
                    code = job.Error,
                    job.Attempt,
                    job.RetryAtUtc,
                    request.RequestId
                }),
                OccurredAtUtc = CollectionFormat.UtcNow()
            });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        finally
        {
            coordinator.Gate.Release();
        }
    }

}







