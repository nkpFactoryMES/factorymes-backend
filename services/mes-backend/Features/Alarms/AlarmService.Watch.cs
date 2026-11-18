using System.Security.Claims;
using FactoryMES.Backend.Features.Orders;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Features.Alarms;

public sealed partial class AlarmService
{
    // jobs-worker의 제한 건수를 받아 규칙 커서를 순환하며 규칙별 독립 트랜잭션으로 판정한다.
    // 동시 배치는 Gate로 직렬화하고 매 규칙 후 추적 상태를 지워 이전 판정의 값을 재사용하지 않는다.
    public async Task<ConnectionWatchResult> WatchAsync(
        DispatchBatchRequest input,
        ConnectionWatchCoordinator coordinator,
        CancellationToken ct
    )
    {
        log.LogDebug("Alarm watch requested with limit {Limit}", input.Limit);
        if (input.Limit is < 1 or > 100)
            throw new OrderFault(
                400,
                "InvalidLimit",
                "감시는 1~100개씩 처리합니다."
            );
        await coordinator.Gate.WaitAsync(ct);
        try
        {
            var query = db.Set<AlarmRule>().AsNoTracking()
                .Where(x => (x.Kind == "COMM_LOST" || x.Kind == "HIGH") && x.Enabled == EF
                    .Parameter(true));
            var ids = await query.Where(x => x.RuleId > coordinator.AfterRuleId)
                .OrderBy(x => x.RuleId).Select(x => x.RuleId).Take(input.Limit).ToListAsync(ct);
            if (ids.Count == 0)
            {
                log.LogDebug("Alarm watch cursor wrapped after rule {RuleId}", coordinator.AfterRuleId);
                coordinator.AfterRuleId = 0;
                ids = await query.OrderBy(x => x.RuleId).Select(x => x.RuleId)
                    .Take(input.Limit).ToListAsync(ct);
            }
            var opened = 0;
            var recovered = 0;
            foreach (var id in ids)
            {
                var change = await WatchRuleAsync(id, ct);
                if (change == "OPENED") opened++;
                if (change == "RECOVERED") recovered++;
                coordinator.AfterRuleId = id;
                // Each rule is one transaction; do not retain stale tracked rows across iterations.
                db.ChangeTracker.Clear();
            }
            log.LogDebug(
                "Alarm watch finished: checked {Checked}, opened {Opened}, recovered {Recovered}",
                ids.Count,
                opened,
                recovered
            );
            return new ConnectionWatchResult(
                ids.Count,
                opened,
                recovered,
                ids.Count - opened - recovered,
                CollectionFormat.UtcNow()
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // DB 예외의 원문·연결 정보는 출력하지 않고 안전한 타입과 마지막 커서만 남긴다.
            log.LogWarning(
                "Alarm watch failed after rule {RuleId}: exception type {ExceptionType}",
                coordinator.AfterRuleId,
                ex.GetType().Name
            );
            throw;
        }
        finally { coordinator.Gate.Release(); }
    }

    private async Task<string?> WatchRuleAsync(long ruleId, CancellationToken ct)
    {
        log.LogDebug("Evaluating alarm rule {RuleId}", ruleId);
        var targetId = await db.Set<AlarmRule>().Where(x => x.RuleId == ruleId)
            .Select(x => x.TargetId).SingleAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var target = (await db.Set<IntegrationTarget>().FromSqlInterpolated(
            $"SELECT * FROM INTEGRATION_TARGET WHERE TARGET_ID = {targetId} FOR UPDATE")
            .ToListAsync(ct)).Single();
        var rule = (await db.Set<AlarmRule>().FromSqlInterpolated(
            $"SELECT * FROM ALARM_RULE WHERE RULE_ID = {ruleId} FOR UPDATE")
            .ToListAsync(ct)).Single();
        var now = CollectionFormat.UtcNow();
        if (!target.Enabled || target.Deleted || !rule.Enabled || rule.SuppressUntilUtc > now
            || target.ActivatedAtUtc is null)
        {
            log.LogDebug("Alarm rule {RuleId} skipped: disabled, suppressed or unactivated", ruleId);
            await tx.CommitAsync(ct);
            return null;
        }
        var connection = await db.Set<EquipmentConnection>().SingleAsync(
            x => x.TargetId == targetId, ct);
        if (rule.Kind == "HIGH")
        {
            var highChange = await WatchHighAsync(
                target,
                rule,
                connection,
                now,
                ct
            );
            await tx.CommitAsync(ct);
            return highChange;
        }
        var cutoff = now.AddSeconds(-connection.StaleSec);
        var fresh = connection.LastValidSampleAtUtc >= cutoff
            && connection.LastValidReceivedAtUtc >= cutoff;
        // 활성화 직후에는 staleSec 유예를 주며 heartbeat만으로 유효 측정의 유예를 연장하지 않는다.
        var overdue = !fresh && target.ActivatedAtUtc <= cutoff;
        var alarm = (await db.Set<Alarm>().FromSqlInterpolated(
            $"SELECT * FROM ALARM WHERE RULE_ID = {ruleId} AND STATUS = 'ACTIVE' FOR UPDATE")
            .ToListAsync(ct)).SingleOrDefault();
        log.LogDebug(
            "COMM_LOST evidence: rule {RuleId}, target {TargetId}, fresh {Fresh}, overdue {Overdue}",
            ruleId,
            targetId,
            fresh,
            overdue
        );
        string? change = null;
        if (overdue && alarm is null)
        {
            alarm = new Alarm
            {
                RuleId = rule.RuleId,
                EquipmentId = rule.EquipmentId,
                Status = "ACTIVE",
                Kind = rule.Kind,
                Severity = rule.Severity,
                RuleVersion = rule.Version,
                OpenedAtUtc = now,
                TriggerSampleAtUtc = connection.LastValidSampleAtUtc,
                RuleSnapshot = CollectionFormat.Canonical(new
                {
                    ruleId = Id(rule.RuleId),
                    targetId = target.TargetCode,
                    equipmentId = Id(rule.EquipmentId),
                    kind = rule.Kind,
                    severity = rule.Severity,
                    version = rule.Version,
                    staleSec = connection.StaleSec,
                    configVersion = target.ConfigVersion
                })
            };
            db.Add(alarm);
            change = "OPENED";
        }
        else if (fresh && alarm is not null)
        {
            alarm.Status = "RECOVERED";
            alarm.RecoveredAtUtc = now < alarm.OpenedAtUtc ? alarm.OpenedAtUtc : now;
            change = "RECOVERED";
        }
        if (change is not null)
        {
            await db.SaveChangesAsync(ct);
            await RecordAlarmChangeAsync(
                alarm!,
                change,
                true,
                null
            );
        }
        await tx.CommitAsync(ct);
        if (change is not null)
        {
            log.LogInformation(
                "COMM_LOST change committed: rule {RuleId}, alarm {AlarmId}, change {Change}",
                ruleId,
                alarm!.AlarmId,
                change
            );
        }
        return change;
    }

    private async Task RecordAlarmChangeAsync(
        Alarm alarm,
        string change,
        bool notify,
        ClaimsPrincipal? user,
        string? servicePrincipal = null
    )
    {
        // 경보 변경에서 Outbox·감사·공통 알림함을 함께 만든다. 최종 커밋은 호출자 소유다.
        log.LogDebug(
            "Staging alarm change: alarm {AlarmId}, rule {RuleId}, change {Change}, notify {Notify}",
            alarm.AlarmId,
            alarm.RuleId,
            change,
            notify
        );
        var eventId = Guid.NewGuid().ToString();
        var correlationId = Guid.NewGuid().ToString();
        var now = CollectionFormat.UtcNow();
        db.Add(new OutboxEvent
        {
            EventId = eventId,
            Type = "mes.alarm.changed.v1",
            AggregateType = "ALARM",
            AggregateId = Id(alarm.AlarmId),
            AggregateVersion = alarm.Version,
            Status = "PENDING",
            Payload = CollectionFormat.Canonical(new
            {
                eventId,
                type = "mes.alarm.changed.v1",
                schemaVersion = 1,
                source = "mes.backend",
                occurredAt = now,
                correlationId,
                data = new
                {
                    alarmId = Id(alarm.AlarmId),
                    equipmentId = Id(alarm.EquipmentId),
                    ruleId = Id(alarm.RuleId),
                    version = alarm.Version,
                    change,
                    status = alarm.Status,
                    severity = alarm.Severity
                }
            })
        });
        db.Add(new AuditEvent
        {
            ActorId = user is null ? null : OrderService.Actor(user),
            Principal = user is null ? servicePrincipal ?? "SERVICE:jobs-worker" : "USER:" + Id(OrderService
                .Actor(user)),
            Action = "alarm." + change.ToLowerInvariant(),
            EntityType = "ALARM",
            EntityId = Id(alarm.AlarmId),
            Detail = CollectionFormat.Canonical(new {
                alarm.Version,
                alarm.Status
            }),
            OccurredAtUtc = now
        });
        if (notify)
        {
            var assigned = db.Set<WorkOrder>().Where(x => x.EquipmentId == alarm.EquipmentId
                && (x.Status == "RELEASED" || x.Status == "RUNNING" || x.Status == "PAUSED"
                    || x.Status == "SUBMITTED")).Select(x => x.AssigneeId);
            var recipients = await db.Set<UserAccount>().Where(x => x.Active == EF.Parameter(true)
                && db.Set<UserRole>().Any(r => r.UserId == x.UserId
                    && (r.Role == "ADMIN" || (r.Role == "WORKER" && assigned.Contains(x.UserId)))))
                .Select(x => x.UserId).ToArrayAsync();
            log.LogDebug(
                "Alarm {AlarmId} notification recipients {Count}",
                alarm.AlarmId,
                recipients.Length
            );
            var equipment = await db.Set<Equipment>().SingleAsync(x => x.EquipmentId == alarm
                .EquipmentId);
            await inbox.CreateAsync(
                eventId,
                change switch
                {
                    "OPENED" => alarm.Kind == "HIGH" ? "측정값 상한 초과" : "설비 통신 끊김",
                    "RECOVERED" => alarm.Kind == "HIGH" ? "측정값 정상 복귀" : "설비 통신 복구",
                    "CLOSED" => "경보 정상 종결",
                    "FORCE_CLOSED" => "경보 강제 종결",
                    _ => "경보 변경"
                },
                equipment.Name + " · " + alarm.Kind + " · " + change,
                alarm.AlarmId,
                recipients,
                correlationId
            );
        }
        await db.SaveChangesAsync();
    }
}






