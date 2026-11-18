using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using FactoryMES.Backend.Features.Notifications;
using FactoryMES.Backend.Features.Orders;
using FactoryMES.Backend.Persistence;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Features.Alarms;

// API의 사용자 명령과 수집·배치의 경보 판정을 받아 Oracle 변경·감사·알림을 함께 저장한다.
// 로그에는 식별자와 처리 상태만 남기고 조치 내용·종결 사유·인증 정보는 기록하지 않는다.
public sealed partial class AlarmService(
    MesDbContext db,
    InboxService inbox,
    ILogger<AlarmService>? logger = null
)
{
    private readonly ILogger<AlarmService> log = logger ?? NullLogger<AlarmService>.Instance;

    private void Check(
        [DoesNotReturnIf(false)] bool valid,
        string message,
        [CallerMemberName] string operation = ""
    )
    {
        if (!valid)
        {
            log.LogWarning("Alarm validation rejected in {Operation}", operation);
            throw new OrderFault(
                422,
                "AlarmValidation",
                message
            );
        }
    }

    private void Conflict(
        bool conflict,
        string message,
        [CallerMemberName] string operation = ""
    )
    {
        if (conflict)
        {
            log.LogWarning("Alarm state or version conflict in {Operation}", operation);
            throw new OrderFault(
                409,
                "AlarmConflict",
                message
            );
        }
    }

    private static string Id(long id) => OrderService.Id(id);
    private static long Parse(string? id) => OrderService.ParseId(id);
    private int Size(int? limit)
    {
        var value = limit ?? 50;
        log.LogDebug("Alarm list requested with limit {Limit}", value);
        if (value is < 1 or > 100) throw new OrderFault(
            400,
            "InvalidLimit",
            "목록은1~100개입니다."
        );
        return value;
    }

    private IQueryable<WorkOrder> CurrentOrders(long actor) => db.Set<WorkOrder>()
        .Where(x => x.AssigneeId == actor && (x.Status == "RELEASED" || x.Status == "RUNNING"
            || x.Status == "PAUSED" || x.Status == "SUBMITTED"));

    private IQueryable<Alarm> VisibleAlarms(ClaimsPrincipal user)
    {
        // 관리자 전체 조회와 작업자의 배정 설비·중지 이력 범위를 같은 조회식에서 제한한다.
        var query = db.Set<Alarm>().AsQueryable();
        log.LogDebug("Resolving visible alarms for actor {ActorId}", OrderService.Actor(user));
        if (user.IsInRole("ADMIN")) return query;
        var actor = OrderService.Actor(user);
        var orders = CurrentOrders(actor);
        return query.Where(x => orders.Any(o => o.EquipmentId == x.EquipmentId)
            || db.Set<WorkPause>().Any(p => p.AlarmId == x.AlarmId && p.RecordedBy == actor));
    }

    public static AlarmView View(Alarm alarm) => new(
        Id(alarm.AlarmId),
        Id(alarm.EquipmentId),
        Id(alarm.RuleId),
        alarm.Status,
        alarm.Kind,
        alarm.Severity,
        alarm.OpenedAtUtc,
        alarm.RecoveredAtUtc,
        alarm.AckBy is null ? null : Id(alarm.AckBy.Value),
        alarm.AckAtUtc,
        alarm.ClosedAtUtc,
        alarm.CloseKind,
        alarm.CloseReason,
        alarm.Version
    );

    private async Task<RuleView> RuleViewAsync(AlarmRule rule)
    {
        var code = await db.Set<IntegrationTarget>().Where(x => x.TargetId == rule.TargetId)
            .Select(x => x.TargetCode).SingleAsync();
        return new RuleView(
            Id(rule.RuleId),
            Id(rule.EquipmentId),
            code,
            rule.MappingId is null ? null : Id(rule.MappingId.Value),
            rule.Kind,
            rule.Severity,
            rule.TriggerHigh is null ? null : CollectionFormat.Number(rule.TriggerHigh.Value),
            rule.ClearBelow is null ? null : CollectionFormat.Number(rule.ClearBelow.Value),
            rule.HoldSec,
            rule.ClearHoldSec,
            rule.Enabled,
            rule.Version,
            rule.SuppressUntilUtc
        );
    }

    private void Validate(RuleWrite input)
    {
        Check(input.Kind is "HIGH" or "COMM_LOST", "경보 종류를 확인해 주세요.");
        if (input.Kind == "COMM_LOST")
        {
            Check(input.MappingId is null && input.TriggerHigh is null && input.ClearBelow is null
                && input.HoldSec is null && input.ClearHoldSec is null,
                "통신 감시에는 수치 임계값을 지정하지 않습니다.");
        }
        else
        {
            Check(input.MappingId is not null && CollectionFormat.TryDecimal(input.TriggerHigh,
                out _)
                && CollectionFormat.TryDecimal(input.ClearBelow, out _)
                && input.HoldSec is >= 0 and <= 3600 && input.ClearHoldSec is >= 0 and <= 3600,
                "수치 매핑·발생/복구 임계값·0~3600초 유지시간이 필요합니다.");
            Check(decimal.Parse(input.ClearBelow!, System.Globalization.CultureInfo
                .InvariantCulture)
                < decimal.Parse(input.TriggerHigh!, System.Globalization.CultureInfo
                    .InvariantCulture),
                "복구 임계값은 발생 임계값보다 작아야 합니다.");
        }
        Check(input.Severity is "INFO" or "WARN" or "CRITICAL", "경보 심각도를 확인해 주세요.");
    }

    private static decimal? Number(string? value) => value is null ? null
        : decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    private async Task ValidateHighMappingAsync(RuleWrite input, long targetId)
    {
        if (input.Kind != "HIGH") return;
        var id = Parse(input.MappingId);
        Check(await db.Set<EquipmentTagMapping>().AnyAsync(x => x.MappingId == id
            && x.TargetId == targetId && x.Enabled == EF.Parameter(true)
            && (x.ValueKind == "NUMBER" || x.ValueKind == "COUNTER")),
            "선택한 연결의 사용 중인 수치 매핑이 필요합니다.");
    }

    public async Task<RuleView> CreateRuleAsync(RuleWrite input, ClaimsPrincipal user)
    {
        // 관리자 입력을 검증하고 설비 연결을 잠근 뒤 규칙과 감사 이력을 원자적으로 저장한다.
        log.LogDebug("Alarm rule create requested by actor {ActorId}", OrderService.Actor(user));
        Validate(input);
        var equipmentId = Parse(input.EquipmentId);
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE PRODUCT IN SHARE ROW EXCLUSIVE MODE");
        var target = (await db.Set<IntegrationTarget>()
            .FromSqlInterpolated($"SELECT * FROM INTEGRATION_TARGET WHERE TARGET_CODE = {input.TargetId} FOR UPDATE")
            .ToListAsync()).SingleOrDefault();
        Check(target is not null && target.Kind == "EQUIPMENT" && !target.Deleted
            && (target.ConnectorType == "equipment-http" || target.ConnectorType == ModbusFormat.Connector)
            && target.EquipmentId == equipmentId,
            "선택한 설비와 연결 대상이 일치하지 않습니다.");
        Check(await db.Set<Equipment>().AnyAsync(x => x.EquipmentId == equipmentId
            && x.Active == EF.Parameter(true) && db.Set<Line>().Any(l => l.LineId == x.LineId
                && l.Active == EF.Parameter(true))), "활성 라인·설비가 필요합니다.");
        await ValidateHighMappingAsync(input, target.TargetId);
        Conflict(input.Kind == "COMM_LOST" && await db.Set<AlarmRule>().AnyAsync(x => x
            .TargetId == target.TargetId && x
            .Kind == "COMM_LOST"),
            "해당 연결의 통신 감시 규칙이 이미 있습니다.");
        var rule = new AlarmRule
        {
            EquipmentId = equipmentId,
            TargetId = target.TargetId,
            Kind = input.Kind,
            MappingId = input.MappingId is null ? null : Parse(input.MappingId),
            TriggerHigh = Number(input.TriggerHigh),
            ClearBelow = Number(input.ClearBelow),
            HoldSec = input.HoldSec,
            ClearHoldSec = input.ClearHoldSec,
            Severity = input.Severity,
            Enabled = input.Enabled
        };
        db.Add(rule);
        await db.SaveChangesAsync();
        RuleAudit(
            rule,
            user,
            "alarm.rule.created"
        );
        await db.SaveChangesAsync();
        var result = await RuleViewAsync(rule);
        await tx.CommitAsync();
        log.LogInformation(
            "Alarm rule created: rule {RuleId}, target {TargetId}, actor {ActorId}, version {Version}",
            rule.RuleId,
            rule.TargetId,
            OrderService.Actor(user),
            rule.Version
        );
        return result;
    }

    public async Task<RuleView> UpdateRuleAsync(
        string id,
        RuleUpdate input,
        ClaimsPrincipal user
    )
    {
        // 수집기와 같은 target → rule 잠금 순서로 정의·버전을 검증해 동시 변경을 막는다.
        log.LogDebug("Alarm rule update requested by actor {ActorId}", OrderService.Actor(user));
        Validate(new RuleWrite(
            input.EquipmentId,
            input.TargetId,
            input.MappingId,
            input.Kind,
            input.Severity,
            input.TriggerHigh,
            input.ClearBelow,
            input.HoldSec,
            input.ClearHoldSec,
            input.Enabled
        ));
        var ruleId = Parse(id);
        await using var tx = await db.Database.BeginTransactionAsync();
        // Watch/ingestion acquire target before rule; use the same order for rule edits.
        var targetId = await db.Set<AlarmRule>().Where(x => x.RuleId == ruleId)
            .Select(x => (long?)x.TargetId).SingleOrDefaultAsync()
            ?? throw new OrderFault(
                404,
                "RuleNotFound",
                "규칙을 찾을 수 없습니다."
            );
        var target = (await db.Set<IntegrationTarget>().FromSqlInterpolated(
            $"SELECT * FROM INTEGRATION_TARGET WHERE TARGET_ID = {targetId} FOR UPDATE")
                .ToListAsync()).Single();
        var rule = (await db.Set<AlarmRule>().FromSqlInterpolated(
            $"SELECT * FROM ALARM_RULE WHERE RULE_ID = {ruleId} FOR UPDATE").ToListAsync())
                .Single();
        Conflict(rule.Version != input.ExpectedVersion || input.ExpectedVersion < 1,
            "규칙이 변경되었습니다.");
        Check(input.EquipmentId == Id(rule.EquipmentId) && input.TargetId == target.TargetCode
            && input.Kind == rule.Kind, "기존 규칙의 설비·연결·종류는 바꿀 수 없습니다.");
        var definition = new RuleWrite(
            input.EquipmentId,
            input.TargetId,
            input.MappingId,
            input.Kind,
            input.Severity,
            input.TriggerHigh,
            input.ClearBelow,
            input.HoldSec,
            input.ClearHoldSec,
            input.Enabled
        );
        await ValidateHighMappingAsync(definition, target.TargetId);
        var mapping = input.MappingId is null ? (long?)null : Parse(input.MappingId);
        var changed = mapping != rule.MappingId || Number(input.TriggerHigh) != rule.TriggerHigh
            || Number(input.ClearBelow) != rule.ClearBelow || input.HoldSec != rule.HoldSec
            || input.ClearHoldSec != rule.ClearHoldSec;
        Conflict(changed && await db.Set<Alarm>().AnyAsync(x => x.RuleId == ruleId && x
            .Status == "ACTIVE"),
            "발생 중 경보의 매핑·임계값·유지시간은 바꿀 수 없습니다.");
        rule.MappingId = mapping;
        rule.TriggerHigh = Number(input.TriggerHigh);
        rule.ClearBelow = Number(input.ClearBelow);
        rule.HoldSec = input.HoldSec;
        rule.ClearHoldSec = input.ClearHoldSec;
        rule.Severity = input.Severity;
        rule.Enabled = input.Enabled;
        db.Entry(rule).Property(x => x.UpdatedAtUtc).IsModified = true;
        RuleAudit(
            rule,
            user,
            "alarm.rule.updated"
        );
        await db.SaveChangesAsync();
        var result = await RuleViewAsync(rule);
        await tx.CommitAsync();
        log.LogInformation(
            "Alarm rule updated: rule {RuleId}, target {TargetId}, actor {ActorId}, version {Version}",
            rule.RuleId,
            rule.TargetId,
            OrderService.Actor(user),
            rule.Version
        );
        return result;
    }

    private void RuleAudit(
        AlarmRule rule,
        ClaimsPrincipal user,
        string action
    )
    {
        db.Add(new AuditEvent
        {
            ActorId = OrderService.Actor(user),
            Principal = "USER:" + Id(OrderService.Actor(user)),
            Action = action,
            EntityType = "ALARM_RULE",
            EntityId = Id(rule.RuleId),
            Detail = CollectionFormat.Canonical(new {
                rule.Enabled,
                rule.Severity,
                rule.Kind,
                rule.MappingId,
                rule.TriggerHigh,
                rule.ClearBelow,
                rule.HoldSec,
                rule.ClearHoldSec
            }),
            OccurredAtUtc = CollectionFormat.UtcNow()
        });
    }

    public async Task<RuleView> GetRuleAsync(string id)
    {
        var key = Parse(id);
        var rule = await db.Set<AlarmRule>().SingleOrDefaultAsync(x => x.RuleId == key)
            ?? throw new OrderFault(
                404,
                "RuleNotFound",
                "규칙을 찾을 수 없습니다."
            );
        return await RuleViewAsync(rule);
    }

    public async Task<PageResult<RuleView>> RulesAsync(
        ClaimsPrincipal user,
        int? limit,
        string? cursor,
        string? equipmentId
    )
    {
        var size = Size(limit);
        var after = cursor is null ? 0 : Parse(cursor);
        var query = db.Set<AlarmRule>().Where(x => x.RuleId > after);
        if (!user.IsInRole("ADMIN"))
        {
            var actor = OrderService.Actor(user);
            var orders = CurrentOrders(actor);
            query = query.Where(x => orders.Any(o => o.EquipmentId == x.EquipmentId));
        }
        if (equipmentId is not null)
        {
            var equipment = Parse(equipmentId);
            query = query.Where(x => x.EquipmentId == equipment);
        }
        var rows = await query.OrderBy(x => x.RuleId).Take(size + 1).ToListAsync();
        var views = new List<RuleView>();
        foreach (var row in rows.Take(size)) views.Add(await RuleViewAsync(row));
        return new PageResult<RuleView>(views.ToArray(), rows.Count > size ? Id(rows[size - 1]
            .RuleId) : null);
    }

    public async Task<PageResult<AlarmView>> ListAsync(
        ClaimsPrincipal user,
        int? limit,
        string? cursor,
        string? equipmentId,
        string? status
    )
    {
        var size = Size(limit);
        var before = cursor is null ? long.MaxValue : Parse(cursor);
        var query = VisibleAlarms(user).Where(x => x.AlarmId < before);
        if (equipmentId is not null)
        {
            var equipment = Parse(equipmentId);
            query = query.Where(x => x.EquipmentId == equipment);
        }
        if (status is not null)
        {
            Check(status is "ACTIVE" or "RECOVERED" or "CLOSED", "경보 상태를 확인해 주세요.");
            query = query.Where(x => x.Status == status);
        }
        var rows = await query.OrderByDescending(x => x.AlarmId).Take(size + 1).ToListAsync();
        return new PageResult<AlarmView>(rows.Take(size).Select(View).ToArray(),
            rows.Count > size ? Id(rows[size - 1].AlarmId) : null);
    }

    public async Task<AlarmView> GetAsync(string id, ClaimsPrincipal user)
    {
        var key = Parse(id);
        var alarm = await VisibleAlarms(user).SingleOrDefaultAsync(x => x.AlarmId == key)
            ?? throw new OrderFault(
                404,
                "AlarmNotFound",
                "경보를 찾을 수 없습니다."
            );
        return View(alarm);
    }

    public async Task<AlarmView> AcknowledgeAsync(
        string id,
        Command input,
        ClaimsPrincipal user
    )
    {
        // 사용자 ACK 요청은 경보 행 잠금·가시 범위·버전 확인 후 최초 확인자만 저장한다.
        var key = Parse(id);
        log.LogDebug(
            "Alarm ACK requested for {AlarmId} by {ActorId}",
            key,
            OrderService.Actor(user)
        );
        await using var tx = await db.Database.BeginTransactionAsync();
        var alarm = (await db.Set<Alarm>().FromSqlInterpolated(
            $"SELECT * FROM ALARM WHERE ALARM_ID = {key} FOR UPDATE").ToListAsync())
                .SingleOrDefault()
            ?? throw new OrderFault(
                404,
                "AlarmNotFound",
                "경보를 찾을 수 없습니다."
            );
        if (!await VisibleAlarms(user).AnyAsync(x => x.AlarmId == key))
            throw new OrderFault(
                404,
                "AlarmNotFound",
                "경보를 찾을 수 없습니다."
            );
        Conflict(alarm.Version != input.ExpectedVersion || input.ExpectedVersion < 1,
            "경보가 변경되었습니다.");
        Conflict(alarm.Status == "CLOSED", "종결된 경보는 변경할 수 없습니다.");
        var newlyAcknowledged = alarm.AckBy is null;
        if (newlyAcknowledged)
        {
            alarm.AckBy = OrderService.Actor(user);
            alarm.AckAtUtc = CollectionFormat.UtcNow();
            await db.SaveChangesAsync();
            await RecordAlarmChangeAsync(
                alarm,
                "ACKNOWLEDGED",
                false,
                user
            );
        }
        log.LogDebug(
            "Alarm ACK resolved for {AlarmId} with owner {AckBy}",
            key,
            alarm.AckBy
        );
        var result = View(alarm);
        await tx.CommitAsync();
        if (newlyAcknowledged)
        {
            log.LogInformation(
                "Alarm acknowledgement committed: alarm {AlarmId}, actor {ActorId}, version {Version}",
                alarm.AlarmId,
                OrderService.Actor(user),
                alarm.Version
            );
        }
        return result;
    }
}




