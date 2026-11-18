using System.Globalization;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;
using Oracle.ManagedDataAccess.Client;

namespace FactoryMES.Backend.Features.Analysis;

// 업무 백엔드 AI 분석 경계. Oracle 스냅샷·작업 상태를 보존하고 게이트웨이 호출·예약·재시도를 조정한다.
public sealed partial class AnalysisService
{
    private static bool IsTransient(Exception error) => error switch
    {
        HttpRequestException http => http.StatusCode is null
            || (int)http.StatusCode is 408 or 429 or >= 500,
        TimeoutException or TaskCanceledException or IOException => true,
        OracleException oracle => oracle.Number is 54 or 60 or 1013 or 3113 or 3114 or 3135
            or 8177 or 12170 or 12514 or 12537 or 12541 or 12543 or 12545 or 12547 or 12560,
        DbUpdateException update when update.InnerException is not null => IsTransient(update
            .InnerException),
        _ => false
    };

    private static bool RetryableCode(string? code) => code is
        "ANALYSIS_TRANSPORT_FAILED" or "ANALYSIS_INPUT_UNAVAILABLE";

    // 일시 오류의 안전한 코드와 현재 시도로 다음 재시각을 계산해 DB에 보존할 재시도 상태를 만든다.
    // Mock은 기존 30초를 유지하고 로컬 GPU는 120초 추론과 경계 처리 여유를 확보한다.
    private static TimeSpan ExecutionWindow(AiSettings settings) =>
        TimeSpan.FromSeconds(settings.ProviderKind == "LOCAL" ? 130 : 30);

    private async Task ScheduleRetryAsync(IntegrationJob job, string code)
    {
        log.LogDebug(
            "{Operation}: {Decision}",
            "ScheduleRetry",
            "입력 처리 시작"
        );
        log.LogWarning(
            "AI 일시 오류 재시도 예약: {JobId}, {FailedAttempt}, {FailureCode}",
            job.JobId,
            job.Attempt,
            code
        );
        var previous = job.Attempt;
        job.Attempt++;
        job.RetryAtUtc = CollectionFormat.UtcNow().AddSeconds(previous == 1 ? 5 : 10);
        var intent = await IntentAsync(job);
        job.DeadlineUtc = job.RetryAtUtc.Value.Add(ExecutionWindow(intent.Configuration.Content.Settings));
        job.State = "QUEUED";
        job.Error = code;
        log.LogDebug(
            "AI 다음 실행 기준: {JobId}, {NextAttempt}, {RetryAtUtc}, {DeadlineUtc}",
            job.JobId,
            job.Attempt,
            job.RetryAtUtc,
            job.DeadlineUtc
        );
        db.Add(new AuditEvent
        {
            Principal = "SERVICE:jobs-worker",
            Action = "analysis.retry.scheduled",
            EntityType = "INTEGRATION_JOB",
            EntityId = job.JobId,
            Detail = CollectionFormat.Canonical(new
            {
                previousAttempt = previous,
                nextAttempt = job.Attempt,
                job.RetryAtUtc,
                code
            }),
            OccurredAtUtc = CollectionFormat.UtcNow()
        });
    }

    // 재시도에 필요한 입력 상태를 복원하고 기존 스냅샷의 불변성을 유지한다.
    private async Task<bool> CaptureRetryInputAsync(IntegrationJob job)
    {
        log.LogDebug(
            "{Operation}: {Decision}",
            "CaptureRetryInputAsync",
            "입력 처리 시작"
        );
        var intent = await IntentAsync(job);
        var target = await db.Set<IntegrationTarget>().SingleAsync(x => x.TargetId == job.TargetId);
        var settings = await db.Set<AiProviderConfig>().SingleAsync(x => x.TargetId == job
            .TargetId);
        Check(target.Enabled && !target.Deleted
            && Compile(target, settings).ConfigHash == intent.Configuration.ConfigHash,
            "분석 설정이 변경되었습니다.");
        var data = await BuildInputAsync(
            Normalize(intent.Request),
            intent.Configuration.Content.Settings.ProviderKind == "LOCAL"
        );
        var coverage = decimal.Parse(data.Coverage, CultureInfo.InvariantCulture);
        var snapshot = new AnalysisSnapshot
        {
            EquipmentId = long.Parse(intent.Request.EquipmentId, CultureInfo.InvariantCulture),
            PeriodStartUtc = intent.Request.PeriodStart,
            PeriodEndUtc = intent.Request.PeriodEnd,
            InputVersion = 1,
            Coverage = coverage,
            Payload = CollectionFormat.Canonical(data),
            InputHash = CollectionFormat.Hash(data)
        };
        db.Add(snapshot);
        await db.SaveChangesAsync();
        job.SnapshotId = snapshot.SnapshotId;
        if (data.SampleCount < 2 || coverage < 0.1m)
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "CaptureRetryInputAsync",
                "분기 일치 [data.SampleCount < 2 || coverage < 0.1m]"
            );
            job.State = "INSUFFICIENT_DATA";
            job.Error = null;
            job.RetryAtUtc = null;
            return false;
        }
        else
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "CaptureRetryInputAsync",
                "분기 불일치 [data.SampleCount < 2 || coverage < 0.1m]"
            );
        }

        return true;
    }
}







