using System.Security.Claims;
using FactoryMES.Backend.Features.Orders;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Features.Alarms;

public sealed partial class AlarmService
{
    private string RequiredText(string? value, string field)
    {
        Check(!string.IsNullOrWhiteSpace(value) && value.Length <= 1000,
            field + "은 공백을 제외한 1~1000자입니다.");
        return value.Trim();
    }

    private async Task<Alarm> LockVisibleAlarmAsync(long key, ClaimsPrincipal user)
    {
        var alarm = (await db.Set<Alarm>().FromSqlInterpolated(
            $"SELECT * FROM ALARM WHERE ALARM_ID = {key} FOR UPDATE").ToListAsync())
                .SingleOrDefault();
        if (alarm is null || !await VisibleAlarms(user).AnyAsync(x => x.AlarmId == key))
            throw new OrderFault(
                404,
                "AlarmNotFound",
                "경보를 찾을 수 없습니다."
            );
        return alarm;
    }

    private void AlarmVersion(Alarm alarm, long expected)
    {
        Conflict(expected < 1 || expected != alarm.Version, "경보가 변경되었습니다.");
        Conflict(alarm.Status == "CLOSED", "종결된 경보는 변경할 수 없습니다.");
    }

    private static AlarmActionView ActionView(AlarmAction action, long alarmVersion) => new(
        Id(action.ActionId),
        Id(action.AlarmId),
        Id(action.OwnerId),
        action.Status,
        action.Content,
        action.Result,
        action.Version,
        alarmVersion
    );

    public async Task<PageResult<AlarmActionView>> ActionsAsync(
        string alarmId,
        ClaimsPrincipal user,
        int? limit,
        string? cursor
    )
    {
        // 브라우저의 경보 ID를 파싱하고 사용자 가시 범위 안에서만 조치를 조회·변경한다.
        var id = Parse(alarmId);
        log.LogDebug(
            "Alarm action request for {AlarmId} by actor {ActorId}",
            id,
            OrderService.Actor(user)
        );
        var size = Size(limit);
        var after = cursor is null ? 0 : Parse(cursor);
        // List and parent version share one Oracle statement snapshot.
        var query = from action in db.Set<AlarmAction>().AsNoTracking()
            join alarm in VisibleAlarms(user).AsNoTracking() on action.AlarmId equals alarm.AlarmId
            where alarm.AlarmId == id && action.ActionId > after
            orderby action.ActionId
            select new {
                action,
                alarm.Version
            };
        var rows = await query.Take(size + 1).ToListAsync();
        if (rows.Count == 0 && !await VisibleAlarms(user).AnyAsync(x => x.AlarmId == id))
            throw new OrderFault(
                404,
                "AlarmNotFound",
                "경보를 찾을 수 없습니다."
            );
        return new PageResult<AlarmActionView>(rows.Take(size)
            .Select(x => ActionView(x.action, x.Version)).ToArray(),
            rows.Count > size ? Id(rows[size - 1].action.ActionId) : null);
    }

    public async Task<AlarmActionView> CreateActionAsync(
        string alarmId,
        ActionCreate input,
        ClaimsPrincipal user
    )
    {
        // 브라우저의 경보 ID를 파싱하고 사용자 가시 범위 안에서만 조치를 조회·변경한다.
        var id = Parse(alarmId);
        log.LogDebug(
            "Alarm action request for {AlarmId} by actor {ActorId}",
            id,
            OrderService.Actor(user)
        );
        var owner = Parse(input.OwnerId);
        var content = RequiredText(input.Content, "조치 내용");
        if (!user.IsInRole("ADMIN") && owner != OrderService.Actor(user))
        {
            log.LogWarning(
                "Alarm action rejected with code {Code} for actor {ActorId}",
                "ActionOwnerForbidden",
                OrderService.Actor(user)
            );
            throw new OrderFault(
                403,
                "ActionOwnerForbidden",
                "작업자는 본인 조치만 등록할 수 있습니다."
            );
        }
        await using var tx = await db.Database.BeginTransactionAsync();
        // Same reference-validation lock as account/work-order changes. Acquire before alarm locks.
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE PRODUCT IN SHARE ROW EXCLUSIVE MODE");
        var alarm = await LockVisibleAlarmAsync(id, user);
        AlarmVersion(alarm, input.ExpectedVersion);
        var orders = CurrentOrders(owner);
        Check(await db.Set<UserAccount>().AnyAsync(x => x.UserId == owner
            && x.Active == EF.Parameter(true) && db.Set<UserRole>().Any(r => r.UserId == owner
                && (r.Role == "ADMIN" || (r.Role == "WORKER"
                    && (orders.Any(o => o.EquipmentId == alarm.EquipmentId)
                        || db.Set<WorkPause>().Any(p => p.AlarmId == id && p
                            .RecordedBy == owner)))))),
            "활성 관리자 또는 해당 경보에 접근 가능한 작업자를 배정해 주세요.");
        var action = new AlarmAction
        {
            AlarmId = id,
            OwnerId = owner,
            Content = content,
            Status = "OPEN"
        };
        db.Add(action);
        db.Entry(alarm).Property(x => x.UpdatedAtUtc).IsModified = true;
        await db.SaveChangesAsync();
        ActionAudit(
            action,
            alarm,
            user,
            "created"
        );
        await db.SaveChangesAsync();
        var result = ActionView(action, alarm.Version);
        await tx.CommitAsync();
        log.LogInformation(
            "Alarm action created: action {ActionId}, alarm {AlarmId}, owner {OwnerId}, actor {ActorId}",
            action.ActionId,
            alarm.AlarmId,
            action.OwnerId,
            OrderService.Actor(user)
        );
        return result;
    }

    public Task<AlarmActionView> StartActionAsync(
        string actionId,
        ActionStart input,
        ClaimsPrincipal user
    ) => ChangeActionAsync(
        actionId,
        input.ExpectedVersion,
        input.ExpectedAlarmVersion,
        null,
        user
    );

    public Task<AlarmActionView> CompleteActionAsync(
        string actionId,
        ActionComplete input,
        ClaimsPrincipal user
    ) => ChangeActionAsync(
        actionId,
        input.ExpectedVersion,
        input.ExpectedAlarmVersion,
        RequiredText(input.Result, "조치 결과"),
        user
    );

    private async Task<AlarmActionView> ChangeActionAsync(
        string actionId,
        long expectedVersion,
        long expectedAlarmVersion,
        string? result,
        ClaimsPrincipal user
    )
    {
        // 조치 ID에서 부모 경보를 찾아 경보 → 조치 순으로 잠근 후 담당자·상태·버전을 검증한다.
        var id = Parse(actionId);
        log.LogDebug(
            "Alarm action change requested for {ActionId} by {ActorId}",
            id,
            OrderService.Actor(user)
        );
        var alarmId = await db.Set<AlarmAction>().Where(x => x.ActionId == id)
            .Select(x => (long?)x.AlarmId).SingleOrDefaultAsync()
            ?? throw new OrderFault(
                404,
                "ActionNotFound",
                "조치를 찾을 수 없습니다."
            );
        await using var tx = await db.Database.BeginTransactionAsync();
        var alarm = await LockVisibleAlarmAsync(alarmId, user);
        var action = (await db.Set<AlarmAction>().FromSqlInterpolated(
            $"SELECT * FROM ALARM_ACTION WHERE ACTION_ID = {id} FOR UPDATE").ToListAsync())
                .Single();
        if (!user.IsInRole("ADMIN") && action.OwnerId != OrderService.Actor(user))
        {
            log.LogWarning(
                "Alarm action rejected with code {Code} for actor {ActorId}",
                "ActionOwnerForbidden",
                OrderService.Actor(user)
            );
            throw new OrderFault(
                403,
                "ActionOwnerForbidden",
                "담당 조치만 변경할 수 있습니다."
            );
        }
        AlarmVersion(alarm, expectedAlarmVersion);
        Conflict(expectedVersion < 1 || expectedVersion != action.Version, "조치가 변경되었습니다.");
        var starting = result is null;
        if (action.Status != (starting ? "OPEN" : "IN_PROGRESS"))
        {
            log.LogWarning(
                "Alarm action rejected with code {Code} for actor {ActorId}",
                "InvalidActionState",
                OrderService.Actor(user)
            );
            throw new OrderFault(
                409,
                "InvalidActionState",
                "조치는 등록 → 시작 → 완료 순서로 처리합니다."
            );
        }
        var now = CollectionFormat.UtcNow();
        action.Status = starting ? "IN_PROGRESS" : "DONE";
        if (starting) action.StartedAtUtc = now < action.CreatedAtUtc ? action.CreatedAtUtc : now;
        else
        {
            action.Result = result;
            action.CompletedAtUtc = now < action.StartedAtUtc ? action.StartedAtUtc : now;
        }
        db.Entry(alarm).Property(x => x.UpdatedAtUtc).IsModified = true;
        await db.SaveChangesAsync();
        ActionAudit(
            action,
            alarm,
            user,
            starting ? "started" : "completed"
        );
        await db.SaveChangesAsync();
        var response = ActionView(action, alarm.Version);
        await tx.CommitAsync();
        log.LogInformation(
            "Alarm action committed: action {ActionId}, alarm {AlarmId}, status {Status}, actor {ActorId}",
            action.ActionId,
            alarm.AlarmId,
            action.Status,
            OrderService.Actor(user)
        );
        return response;
    }

    private void ActionAudit(
        AlarmAction action,
        Alarm alarm,
        ClaimsPrincipal user,
        string change
    )
    {
        db.Add(new AuditEvent
        {
            ActorId = OrderService.Actor(user),
            Principal = "USER:" + Id(OrderService.Actor(user)),
            Action = "alarm.action." + change,
            EntityType = "ALARM_ACTION",
            EntityId = Id(action.ActionId),
            Detail = CollectionFormat.Canonical(new
            {
                alarmId = Id(alarm.AlarmId),
                alarmVersion = alarm.Version,
                actionVersion = action.Version,
                status = action.Status,
                ownerId = Id(action.OwnerId)
            }),
            OccurredAtUtc = CollectionFormat.UtcNow()
        });
    }

    public async Task<AlarmView> CloseAsync(
        string alarmId,
        ReasonCommand input,
        ClaimsPrincipal user,
        bool forced
    )
    {
        // 브라우저의 경보 ID를 파싱하고 사용자 가시 범위 안에서만 조치를 조회·변경한다.
        var id = Parse(alarmId);
        log.LogDebug(
            "Alarm action request for {AlarmId} by actor {ActorId}",
            id,
            OrderService.Actor(user)
        );
        // 정상 종결은 복구·ACK·완료 조치를 요구하고 강제 종결은 규칙을 30분 억제한다.
        // 사유는 감사 가능한 DB 필드에만 보관하며 애플리케이션 로그로 내보내지 않는다.
        var reason = RequiredText(input.Reason, "종결 사유");
        await using var tx = await db.Database.BeginTransactionAsync();
        AlarmRule? rule = null;
        if (forced)
        {
            // Watch uses target -> rule -> alarm. Never acquire rule after locking alarm.
            var link = await db.Set<Alarm>().Where(x => x.AlarmId == id)
                .Join(
                    db.Set<AlarmRule>(),
                    a => a.RuleId,
                    r => r.RuleId,
                    (a, r) => new {
                        r.RuleId,
                        r.TargetId
                    }
                ).SingleOrDefaultAsync()
                ?? throw new OrderFault(
                    404,
                    "AlarmNotFound",
                    "경보를 찾을 수 없습니다."
                );
            await db.Set<IntegrationTarget>().FromSqlInterpolated(
                $"SELECT * FROM INTEGRATION_TARGET WHERE TARGET_ID = {link.TargetId} FOR UPDATE")
                    .ToListAsync();
            rule = (await db.Set<AlarmRule>().FromSqlInterpolated(
                $"SELECT * FROM ALARM_RULE WHERE RULE_ID = {link.RuleId} FOR UPDATE")
                    .ToListAsync()).Single();
        }
        var alarm = await LockVisibleAlarmAsync(id, user);
        AlarmVersion(alarm, input.ExpectedVersion);
        Conflict(await db.Set<AlarmAction>().AnyAsync(x => x.AlarmId == id && x.Status != "DONE"),
            "미완료 조치가 있어 종결할 수 없습니다.");
        if (forced) Conflict(alarm.Status != "ACTIVE", "ACTIVE 경보만 강제 종결할 수 있습니다.");
        else
        {
            Conflict(alarm.Status != "RECOVERED" || alarm.AckBy is null
                || !await db.Set<AlarmAction>().AnyAsync(x => x.AlarmId == id && x
                    .Status == "DONE"),
                "정상 종결은 복구·ACK·완료 조치 1건 이상이 필요합니다.");
        }
        var now = CollectionFormat.UtcNow();
        alarm.Status = "CLOSED";
        alarm.CloseKind = forced ? "FORCED" : "NORMAL";
        alarm.ClosedBy = OrderService.Actor(user);
        alarm.ClosedAtUtc = new[] {
            now,
            alarm.OpenedAtUtc,
            alarm.RecoveredAtUtc ?? alarm.OpenedAtUtc
        }.Max();
        alarm.CloseReason = reason;
        if (rule is not null) rule.SuppressUntilUtc = now.AddMinutes(30);
        await db.SaveChangesAsync();
        await RecordAlarmChangeAsync(
            alarm,
            forced ? "FORCE_CLOSED" : "CLOSED",
            true,
            user
        );
        var response = View(alarm);
        await tx.CommitAsync();
        log.LogInformation(
            "Alarm closed: alarm {AlarmId}, kind {CloseKind}, actor {ActorId}, rule {RuleId}",
            alarm.AlarmId,
            alarm.CloseKind,
            OrderService.Actor(user),
            alarm.RuleId
        );
        return response;
    }
}


