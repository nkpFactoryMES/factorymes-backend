using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Features.Alarms;

public sealed partial class AlarmService
{
    // 수집 서비스가 측정값 저장 후 호출한다. 대상 잠금과 트랜잭션은 수집 서비스가 소유한다.
    // 활성 HIGH 규칙을 순서대로 잠그고 같은 설정 버전의 유효 측정 이력으로 발생·복구를 판단한다.
    internal async Task EvaluateHighTargetAsync(IntegrationTarget target)
    {
        log.LogDebug("HIGH evaluation requested for target {TargetId}", target.TargetId);
        if (!target.Enabled || target.Deleted || target.ActivatedAtUtc is null) return;
        var ids = await db.Set<AlarmRule>().AsNoTracking().Where(x => x.TargetId == target.TargetId
            && x.Kind == "HIGH" && x.Enabled == EF.Parameter(true))
            .OrderBy(x => x.RuleId).Select(x => x.RuleId).ToArrayAsync();
        log.LogDebug(
            "HIGH target {TargetId} has {RuleCount} enabled rules",
            target.TargetId,
            ids.Length
        );
        if (ids.Length == 0) return;
        var connection = await db.Set<EquipmentConnection>().SingleAsync(x => x.TargetId == target
            .TargetId);
        foreach (var id in ids)
        {
            var rule = (await db.Set<AlarmRule>().FromSqlInterpolated(
                $"SELECT * FROM ALARM_RULE WHERE RULE_ID = {id} FOR UPDATE").ToListAsync())
                    .Single();
            var now = CollectionFormat.UtcNow();
            if (!rule.Enabled || rule.SuppressUntilUtc > now)
            {
                log.LogDebug("HIGH rule {RuleId} skipped: disabled or suppressed", rule.RuleId);
                continue;
            }
            await WatchHighAsync(
                target,
                rule,
                connection,
                now,
                CancellationToken.None,
                "SERVICE:equipment-connector"
            );
        }
    }

    private async Task<string?> WatchHighAsync(
        IntegrationTarget target,
        AlarmRule rule,
        EquipmentConnection connection,
        DateTime now,
        CancellationToken ct,
        string servicePrincipal = "SERVICE:jobs-worker"
    )
    {
        // 현재 매핑과 설정 버전에 속한 최근 측정만 사용하고 유지시간·신선도 증거가 부족하면 유지한다.
        log.LogDebug(
            "Inspecting HIGH rule {RuleId} for target {TargetId}",
            rule.RuleId,
            target.TargetId
        );
        var mapping = await db.Set<EquipmentTagMapping>().SingleOrDefaultAsync(
            x => x.MappingId == rule.MappingId && x.TargetId == target.TargetId, ct);
        if (mapping is null || !mapping.Enabled || mapping.ValueKind == "STATE")
        {
            log.LogDebug(
                "HIGH rule {RuleId} skipped: mapping unavailable for numeric evaluation",
                rule.RuleId
            );
            return null;
        }
        var alarm = (await db.Set<Alarm>().FromSqlInterpolated(
            $"SELECT * FROM ALARM WHERE RULE_ID = {rule.RuleId} AND STATUS = 'ACTIVE' FOR UPDATE")
            .ToListAsync(ct)).SingleOrDefault();
        var recovering = alarm is not null;
        var hold = recovering ? rule.ClearHoldSec!.Value : rule.HoldSec!.Value;
        var threshold = recovering ? rule.ClearBelow!.Value : rule.TriggerHigh!.Value;
        var anchor = rule.UpdatedAtUtc > target.ActivatedAtUtc ? rule.UpdatedAtUtc : target
            .ActivatedAtUtc!.Value;
        if (rule.SuppressUntilUtc is not null && rule.SuppressUntilUtc > anchor)
            anchor = rule.SuppressUntilUtc.Value;
        if (recovering && alarm!.OpenedAtUtc > anchor) anchor = alarm.OpenedAtUtc;
        var floor = now.AddSeconds(-hold - connection.StaleSec);
        if (anchor > floor) floor = anchor;
        var rows = await (from metric in db.Set<TelemetryMetric>().AsNoTracking()
            join sample in db.Set<TelemetryEvent>().AsNoTracking() on metric
                .TelemetryId equals sample.TelemetryId
            where metric.MappingId == mapping.MappingId && sample.TargetId == target.TargetId
                && sample.ConfigVersion == target.ConfigVersion && sample.OccurredAtUtc >= floor
            orderby sample.OccurredAtUtc descending, sample.TelemetryId descending
            select new HighReading(
                sample.OccurredAtUtc,
                sample.ReceivedAtUtc,
                metric.NumericValue,
                metric.Quality
            ))
            .Take(10001).ToListAsync(ct);
        // 최대 조회량을 넘거나 연속 유효 증거가 부족하면 경보 상태를 바꾸지 않는다.
        log.LogDebug(
            "HIGH evidence loaded: rule {RuleId}, rows {Count}, recovering {Recovering}, hold {HoldSec}",
            rule.RuleId,
            rows.Count,
            recovering,
            hold
        );
        if (rows.Count > 10000)
        {
            log.LogWarning("HIGH evidence limit exceeded for rule {RuleId}", rule.RuleId);
        }
        if (rows.Count > 10000 || !HighThresholdEvidence.Supports(
            rows,
            threshold,
            recovering,
            hold,
            connection.StaleSec,
            now
        ))
        {
            log.LogDebug("HIGH rule {RuleId} unchanged: continuous evidence insufficient", rule.RuleId);
            return null;
        }
        var latest = rows[0];
        var change = recovering ? "RECOVERED" : "OPENED";
        if (recovering)
        {
            alarm!.Status = "RECOVERED";
            alarm.RecoveredAtUtc = now < alarm.OpenedAtUtc ? alarm.OpenedAtUtc : now;
        }
        else
        {
            alarm = new Alarm
            {
                RuleId = rule.RuleId,
                EquipmentId = rule.EquipmentId,
                Kind = "HIGH",
                Status = "ACTIVE",
                Severity = rule.Severity,
                RuleVersion = rule.Version,
                OpenedAtUtc = now,
                TriggerValue = latest.Value,
                TriggerSampleAtUtc = latest.SampledAt,
                RuleSnapshot = CollectionFormat.Canonical(new
                {
                    ruleId = Id(rule.RuleId),
                    targetId = target.TargetCode,
                    equipmentId = Id(rule.EquipmentId),
                    mappingId = Id(mapping.MappingId),
                    metricCode = mapping.MetricCode,
                    unit = mapping.Unit,
                    kind = rule.Kind,
                    severity = rule.Severity,
                    version = rule.Version,
                    triggerHigh = CollectionFormat.Number(rule.TriggerHigh!.Value),
                    clearBelow = CollectionFormat.Number(rule.ClearBelow!.Value),
                    holdSec = rule.HoldSec,
                    clearHoldSec = rule.ClearHoldSec,
                    configVersion = target.ConfigVersion
                })
            };
            db.Add(alarm);
        }
        await db.SaveChangesAsync(ct);
        await RecordAlarmChangeAsync(
            alarm!,
            change,
            true,
            null,
            servicePrincipal
        );
        // 수집·배치 호출자가 최종 커밋하므로 이 로그는 변경 저장과 이벤트 준비 완료를 뜻한다.
        log.LogInformation(
            "HIGH change staged: alarm {AlarmId}, rule {RuleId}, target {TargetId}, change {Change}",
            alarm!.AlarmId,
            rule.RuleId,
            target.TargetId,
            change
        );
        return change;
    }
}



