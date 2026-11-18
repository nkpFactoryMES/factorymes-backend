using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryMES.Backend.Persistence;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;
namespace FactoryMES.Backend.Features.Orders;
public sealed class OrderFault(
    int status,
    string code,
    string message
) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
public sealed partial class OrderService
{
    private readonly MesDbContext db;

    private readonly AutoProductionService auto;
    private readonly ILogger<OrderService> log;

    // Cookie 인증 후의 업무 요청을 받아 권한·버전·상태를 검사하고 같은 Oracle 트랜잭션에 원장을 남긴다.
    public OrderService(
        MesDbContext db,
        AutoProductionService auto,
        ILogger<OrderService> log
    )
    {
        this.db = db;
        this.auto = auto;
        this.log = log;
    }

    private const long MaxQty = 999999999999;
    public static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);
    public static long ParseId(string? value)
    {
        if (value is null || value.Length > 19 || value.StartsWith('0') || !long.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var id
        ) || id <= 0)
        {
            throw new OrderFault(
                400,
                "InvalidId",
                "올바른 ID가 필요합니다."
            );
        }
        return id;
    }

    public static long Actor(ClaimsPrincipal user) =>
        ParseId(user.FindFirstValue(ClaimTypes.NameIdentifier));
    private void Require([DoesNotReturnIf(false)] bool valid, string message)
    {
        log.LogDebug(
            "Order rule evaluated Passed={Passed} Rule={Rule}",
            valid,
            message
        );
        if (!valid)
        {
            log.LogWarning("Order rule rejected Rule={Rule}", message);
            throw new OrderFault(
                422,
                "BusinessValidation",
                message
            );
        }
    }

    private void Version(WorkOrder order, long expected)
    {
        log.LogDebug(
            "Order version checked: order {OrderId}, expected {Expected}, actual {Actual}",
            order.OrderId,
            expected,
            order.Version
        );
        if (expected <= 0 || order.Version != expected)
        {
            log.LogWarning("Order version conflict for order {OrderId}", order.OrderId);
            throw new OrderFault(
                409,
                "VersionConflict",
                "다른 변경이 있습니다. 새로고침해 주세요."
            );
        }
    }

    private void State(WorkOrder order, params string[] allowed)
    {
        log.LogDebug(
            "Order state checked: order {OrderId}, status {Status}",
            order.OrderId,
            order.Status
        );
        if (!allowed.Contains(order.Status))
        {
            log.LogWarning("Order state conflict for order {OrderId}", order.OrderId);
            throw new OrderFault(
                409,
                "StateConflict",
                "현재 작업 상태에서 실행할 수 없습니다."
            );
        }
    }

    private void Scope(WorkOrder order, ClaimsPrincipal user)
    {
        if (!user.IsInRole("ADMIN") && (order.AssigneeId != Actor(user) || order.Status == "DRAFT"))
        {
            log.LogWarning(
                "Order scope rejected: order {OrderId}, actor {ActorId}",
                order.OrderId,
                Actor(user)
            );
            throw new OrderFault(
                404,
                "OrderNotFound",
                "작업지시를 찾을 수 없습니다."
            );
        }
    }

    private async Task<WorkOrder> LoadAsync(
        long id,
        ClaimsPrincipal user,
        bool locked
    )
    {
        // 작업 ID와 인증 사용자를 받아 필요 시 Product→Equipment→Order 순으로 잠그고 담당 범위를 확인한다.
        log.LogDebug("Order LoadAsync entered by actor {ActorId}", Actor(user));

        if (locked)
        {
            // 기존 기준정보 잠금보다 먼저 설비를 잡지 않는다: Product→Equipment→Order.
            await db.Database.ExecuteSqlRawAsync("LOCK TABLE PRODUCT IN SHARE ROW EXCLUSIVE MODE");
            var equipment = await db.Set<WorkOrder>().Where(x => x.OrderId == id)
                .Select(x => (long?)x.EquipmentId).SingleOrDefaultAsync();
            if (equipment is { } equipmentId)
                await AutoProductionService.LockEquipmentAsync(db, equipmentId);
        }
        log.LogDebug(
            "Order load Order={OrderId} Actor={ActorId} Locked={Locked}",
            id,
            Actor(user),
            locked
        );
        var rows = locked ? await db.Set<WorkOrder>()
            .FromSqlInterpolated($"SELECT * FROM WORK_ORDER WHERE ORDER_ID = {id} FOR UPDATE")
            .ToListAsync() : await db.Set<WorkOrder>()
            .Where(x => x.OrderId == id)
            .ToListAsync();
        var order = rows.SingleOrDefault() ?? throw new OrderFault(
            404,
            "OrderNotFound",
            "작업지시를 찾을 수 없습니다."
        );
        Scope(order, user);
        Require(order.CountMode is "MANUAL" or "AUTO", "작업 집계 방식을 확인하세요.");
        return order;
    }

    private async Task ValidateReferencesAsync(
        long productId,
        long equipmentId,
        long assigneeId
    )
    {
        // 작업 등록·착수에서 품목·라인·설비·작업자 활성 상태를 기준정보 변경과 같은 잠금 안에서 검증한다.
        log.LogDebug("Order ValidateReferencesAsync entered");

        // Serialize reference validation with master-data changes until this transaction commits.
        // One shared lock keeps portfolio-scale master changes and order assignment atomic.
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE PRODUCT IN SHARE ROW EXCLUSIVE MODE");
        Require(
            await db.Set<Product>().AnyAsync(x => x.ProductId == productId
                && x.Active == EF.Parameter(true)),
            "활성 품목이 필요합니다."
        );
        Require(
            await db.Set<Equipment>().AnyAsync(
                x => x.EquipmentId == equipmentId
                    && x.Active == EF.Parameter(true)
                    && db.Set<Line>().Any(l => l.LineId == x.LineId
                    && l.Active == EF.Parameter(true))
            ),
            "활성 라인에 속한 설비가 필요합니다."
        );
        Require(
            await db.Set<UserAccount>().AnyAsync(
                x => x.UserId == assigneeId
                    && x.Active == EF.Parameter(true)
                    && (x.LockUntilUtc == null
                    || x.LockUntilUtc <= DateTime.UtcNow)
            ),
            "활성 작업자 계정이 필요합니다."
        );
        Require(
            await db.Set<UserRole>().AnyAsync(x => x.UserId == assigneeId
                && x.Role == "WORKER")
                && !await db.Set<UserRole>().AnyAsync(x => x.UserId == assigneeId
                && x.Role == "ADMIN"),
            "관리자 권한이 없는 작업자를 배정해 주세요."
        );
    }

    public async Task<OrderView> CreateAsync(OrderCreate input, ClaimsPrincipal user)
    {
        // 관리자 화면의 등록 입력을 받아 참조·형식을 검증하고 업무 데이터와 감사 이력을 함께 저장한다.
        log.LogDebug("Order CreateAsync entered by actor {ActorId}", Actor(user));

        Require(input.CountMode is "MANUAL" or "AUTO", "작업 집계 방식을 확인하세요.");
        Require(input.TargetQty is> 0 and <= MaxQty, "목표 수량 범위를 확인해 주세요.");
        Require(
            input.PlannedStart.Kind == DateTimeKind.Utc
                && input.PlannedEnd.Kind == DateTimeKind.Utc
                && input.PlannedEnd > input.PlannedStart,
            "계획 시간은 UTC이며 종료가 시작보다 늦어야 합니다."
        );
        Require(
            input.OperationLabel is null || input.OperationLabel.Length <= 100,
            "작업명은 100자 이내입니다."
        );
        Require(input.Checklist is not null && input.Checklist.Length <= 30, "점검표는 30개 이내입니다.");
        Require(
            input.Checklist!.All(
                x => x is not null
                    && x.Seq is >= 1 and <= 100
                    && !string.IsNullOrWhiteSpace(x.Label)
                    && x.Label.Length <= 200
            ) && input.Checklist.Select(x => x.Seq).Distinct().Count() == input.Checklist.Length,
            "점검표 순번·이름을 확인해 주세요."
        );
        var product = ParseId(input.ProductId);
        var equipment = ParseId(input.EquipmentId);
        var assignee = ParseId(input.AssigneeId);
        await using var tx = await db.Database.BeginTransactionAsync();
        await ValidateReferencesAsync(
            product,
            equipment,
            assignee
        );
        var mapping = input.CounterMappingId is null ? (long?)null : ParseId(input
            .CounterMappingId);
        await auto.ValidateMappingAsync(
            equipment,
            input.CountMode,
            mapping
        );
        var order = new WorkOrder
        {
            OrderNo = "WO-" + Guid.NewGuid().ToString("N"),
            ProductId = product,
            EquipmentId = equipment,
            AssigneeId = assignee,
            OperationLabel = input.OperationLabel,
            TargetQty = input.TargetQty,
            CountMode = input.CountMode,
            CounterMappingId = mapping,
            PlannedStartUtc = input.PlannedStart,
            PlannedEndUtc = input.PlannedEnd,
            Status = "DRAFT",
            CreatedBy = Actor(user)
        };
        db.Add(order);
        await db.SaveChangesAsync();
        foreach (var item in input.Checklist)
        {
            db.Add(
                new WorkCheckItem {
                    OrderId = order.OrderId,
                    Seq = item.Seq,
                    Label = item.Label,
                    Required = item.Required
                }
            );
        }
        Audit(
            order,
            user,
            "order.created",
            null
        );
        await db.SaveChangesAsync();
        var result = await ViewAsync(order);
        await tx.CommitAsync();
        log.LogInformation(
            "CreateAsync committed: order {OrderId}, actor {ActorId}, status {Status}, version {Version}",
            order.OrderId,
            Actor(user),
            order.Status,
            order.Version
        );
        return result;
    }

    public async Task<OrderView> GetAsync(string id, ClaimsPrincipal user)
    {
        // 화면의 조회 요청을 받아 접근 가능한 단건을 조회하고 표시용 응답으로 변환한다.
        log.LogDebug("Order GetAsync entered by actor {ActorId}", Actor(user));

        var order = await LoadAsync(
            ParseId(id),
            user,
            false
        );
        return await ViewAsync(order);
    }

    public async Task<PageResult<OrderView>> ListAsync(
        ClaimsPrincipal user,
        int? limit,
        string? cursor,
        string? status,
        string? equipmentId,
        string? assigneeId
    )
    {
        // 화면의 페이지 크기·커서와 필터를 검증한 뒤 허용된 범위에서 다음 페이지를 조회한다.
        log.LogDebug("Order ListAsync entered by actor {ActorId}", Actor(user));

        var size = limit ?? 50;
        Require(size is >= 1 and <= 100, "목록 크기는 1~100입니다.");
        var query = db.Set<WorkOrder>().Where(x => x.CountMode == "MANUAL" || x
            .CountMode == "AUTO");
        if (!user.IsInRole("ADMIN"))
        {
            var actor = Actor(user);
            query = query.Where(x => x.AssigneeId == actor && x.Status != "DRAFT");
        }
        if (equipmentId is not null)
        {
            var equipment = ParseId(equipmentId);
            query = query.Where(x => x.EquipmentId == equipment);
        }
        if (assigneeId is not null)
        {
            var assignee = ParseId(assigneeId);
            query = query.Where(x => x.AssigneeId == assignee);
        }
        if (cursor is not null)
        {
            var after = ParseId(cursor);
            query = query.Where(x => x.OrderId < after);
        }
        if (status is not null)
        {
            Require(
                new[] {
                    "DRAFT",
                    "RELEASED",
                    "RUNNING",
                    "PAUSED",
                    "SUBMITTED",
                    "CLOSED",
                    "CANCELLED"
                }.Contains(status),
                "상태 필터를 확인해 주세요."
            );
            query = query.Where(x => x.Status == status);
        }
        var rows = await query.OrderByDescending(x => x.OrderId).Take(size + 1).ToListAsync();
        var result = new List<OrderView>();
        foreach (var order in rows.Take(size))
        {
            result.Add(await ViewAsync(order));
        }
        return new PageResult<OrderView>(
            result.ToArray(),
            rows.Count > size ? Id(rows[size - 1].OrderId) : null
        );
    }

    private async Task<(long Total, long Reject)> TotalsAsync(long id)
    {
        // 저장된 원장을 읽어 원본 종류와 역분개 부호를 적용해 총수량·불량 순합계를 계산한다.
        log.LogDebug("Order TotalsAsync entered");

        var entries = await db.Set<ProductionEntry>().Where(x => x.OrderId == id).ToListAsync();
        long total = 0;
        long reject = 0;
        foreach (var item in entries)
        {
            var original = item.Kind == "REVERSAL"
                ? entries.Single(x => x.EntryId == item.ReversalOf)
                : item;
            var sign = item.Kind is "REVERSAL" or "TOTAL_ADJUST_DOWN" ? -1 : 1;
            if (original.Kind == "REJECT")
            {
                reject = checked(reject + sign * item.Qty);
            }
            else
            {
                total = checked(total + sign * item.Qty);
            }
        }
        log.LogDebug(
            "Order totals computed: order {OrderId}, total {Total}, reject {Reject}",
            id,
            total,
            reject
        );
        return (total, reject);
    }

    private async Task<OrderView> ViewAsync(WorkOrder order)
    {
        // 업무 엔터티와 하위 이력을 조회해 화면용 DTO를 만든다. 메모·사유 본문은 로그로 복제하지 않는다.
        log.LogDebug("Order ViewAsync entered");

        var items = await db.Set<WorkCheckItem>()
            .Where(x => x.OrderId == order.OrderId)
            .OrderBy(x => x.Seq)
            .ToListAsync();
        var totals = await TotalsAsync(order.OrderId);
        return new OrderView(
            Id(order.OrderId),
            order.OrderNo,
            Id(order.ProductId),
            Id(order.EquipmentId),
            Id(order.AssigneeId),
            order.OperationLabel,
            order.TargetQty,
            order.CountMode,
            order.CounterMappingId?.ToString(),
            order.PlannedStartUtc,
            order.PlannedEndUtc,
            order.Status,
            order.Version,
            items.Select(
                x => new ChecklistItem(
                    Id(x.CheckId),
                    x.Seq,
                    x.Label,
                    x.Required,
                    x.Checked,
                    x.CheckedAtUtc,
                    x.CommentText
                )
            ).ToArray(),
            order.StartCounter is { } startCounter ? CollectionFormat.Number(startCounter) : null,
            order.EndCounter is { } endCounter ? CollectionFormat.Number(endCounter) : null,
            order.CounterInvalid,
            totals.Total,
            totals.Reject,
            totals.Total - totals.Reject,
            order.CompletionReason,
            await auto.ViewAsync(order)
        );
    }

    public async Task<OrderView> TransitionAsync(
        string id,
        long expected,
        string action,
        string? reason,
        ClaimsPrincipal user
    )
    {
        // 화면의 발행·착수·제출·마감 명령을 받아 부모 잠금 안에서 상태·버전을 검증한다. 사유 본문은 로그에 남기지 않는다.
        log.LogDebug("Order TransitionAsync entered by actor {ActorId}", Actor(user));

        await using var tx = await db.Database.BeginTransactionAsync();
        var order = await LoadAsync(
            ParseId(id),
            user,
            true
        );
        Version(order, expected);
        Require(reason is null || reason.Length <= 1000, "사유는 1000자 이내입니다.");
        var previous = order.Status;
        var now = DateTime.UtcNow;
        // A host clock correction must not put a later lifecycle event before its predecessor.
        if (order.StartedAtUtc is { } started && now < started)
        {
            now = started;
        }
        if (order.SubmittedAtUtc is { } submitted && now < submitted)
        {
            now = submitted;
        }
        now = now.AddTicks(-(now.Ticks % 10));
        log.LogDebug(
            "Order transition dispatch: order {OrderId}, current {Status}",
            order.OrderId,
            previous
        );
        switch (action)
        {
            case "release":
                log.LogDebug("Order release branch for order {OrderId}", order.OrderId);
                State(order, "DRAFT");
                await ValidateReferencesAsync(
                    order.ProductId,
                    order.EquipmentId,
                    order.AssigneeId
                );
                order.Status = "RELEASED";
                break;
            case "start":
                log.LogDebug("Order start branch for order {OrderId}", order.OrderId);
                State(order, "RELEASED");
                await ValidateReferencesAsync(
                    order.ProductId,
                    order.EquipmentId,
                    order.AssigneeId
                );
                Require(
                    !await db.Set<WorkCheckItem>().AnyAsync(
                        x => x.OrderId == order.OrderId
                            && x.Required == EF.Parameter(true)
                            && x.Checked == EF.Parameter(false)
                    ),
                    "필수 점검을 완료해 주세요."
                );
                // Lock equipment as well as order, so two starts serialize across different orders.
                await db.Set<Equipment>()
                    .FromSqlInterpolated(
                        $"SELECT * FROM EQUIPMENT WHERE EQUIPMENT_ID = {order.EquipmentId} FOR UPDATE"
                    )
                    .ToListAsync();
                Require(
                    !await db.Set<WorkOrder>().AnyAsync(
                        x => x.EquipmentId == order.EquipmentId
                            && x.OrderId != order.OrderId
                            && (x.Status == "RUNNING"
                            || x.Status == "PAUSED")
                    ),
                    "설비를 다른 작업에서 사용 중입니다."
                );
                order.Status = "RUNNING";
                order.StartedAtUtc = now;
                await auto.ArmAsync(order, now);
                break;
            case "submit":
                log.LogDebug("Order submit branch for order {OrderId}", order.OrderId);
                State(
                    order,
                    "RUNNING",
                    "PAUSED"
                );
                await auto.RequireReadyAsync(order);
                var totals = await TotalsAsync(order.OrderId);
                Require(totals.Total >= totals.Reject
                    && totals.Reject >= 0, "불량 수량은 총수량을 넘을 수 없습니다.");
                Require(
                    totals.Total == order.TargetQty || !string.IsNullOrWhiteSpace(reason),
                    "목표 수량과 다르면 제출 사유가 필요합니다."
                );
                var pauses = await db.Set<WorkPause>()
                    .Where(x => x.OrderId == order.OrderId
                    && x.EndedAtUtc == null)
                    .ToListAsync();
                foreach (var pause in pauses)
                {
                    if (now < pause.StartedAtUtc)
                    {
                        now = pause.StartedAtUtc;
                    }
                    pause.EndedAtUtc = now;
                }
                order.Status = "SUBMITTED";
                order.SubmittedAtUtc = now;
                order.CompletionReason = reason;
                break;
            case "close":
                log.LogDebug("Order close branch for order {OrderId}", order.OrderId);
                State(order, "SUBMITTED");
                Require(Actor(user) != order.AssigneeId, "자신에게 배정된 작업을 마감할 수 없습니다.");
                Require(!string.IsNullOrWhiteSpace(reason), "마감 검토 사유가 필요합니다.");
                Require(
                    !await db.Set<CorrectionRequest>().AnyAsync(x => x.OrderId == order.OrderId
                        && x.Status == "OPEN"),
                    "열린 정정 요청을 먼저 해결해 주세요."
                );
                var closeTotals = await TotalsAsync(order.OrderId);
                Require(
                    closeTotals.Total >= closeTotals.Reject && closeTotals.Reject >= 0,
                    "실적 합계를 확인해 주세요."
                );
                order.Status = "CLOSED";
                order.ClosedAtUtc = now;
                break;
            default:
                log.LogWarning("Order TransitionAsync rejected with code {Code}", "UnknownCommand");
                throw new OrderFault(
                    400,
                    "UnknownCommand",
                    "지원하지 않는 명령입니다."
                );
        }
        db.Add(
            new OrderTransition {
                OrderId = order.OrderId,
                FromStatus = previous,
                ToStatus = order.Status,
                ActorId = Actor(user),
                Principal = "USER:" + Id(Actor(user)),
                Reason = reason,
                OccurredAtUtc = now
            }
        );
        Audit(
            order,
            user,
            "order." + action,
            reason
        );
        await db.SaveChangesAsync();
        var result = await ViewAsync(order);
        await tx.CommitAsync();
        log.LogInformation(
            "TransitionAsync committed: order {OrderId}, actor {ActorId}, status {Status}, version {Version}",
            order.OrderId,
            Actor(user),
            order.Status,
            order.Version
        );
        return result;
    }

    public async Task<OrderView> ChecklistAsync(
        string id,
        ChecklistUpdate input,
        ClaimsPrincipal user
    )
    {
        // 작업자 점검 입력을 받아 해당 작업 소속 항목인지 검증하고 체크 상태와 수행자를 저장한다.
        log.LogDebug("Order ChecklistAsync entered by actor {ActorId}", Actor(user));

        await using var tx = await db.Database.BeginTransactionAsync();
        var order = await LoadAsync(
            ParseId(id),
            user,
            true
        );
        Version(order, input.ExpectedVersion);
        State(order, "RELEASED");
        Require(input.Items is { Length: >= 1 and <= 30 }, "점검 항목을 확인해 주세요.");
        Require(
            input.Items!.All(x => x is not null)
                && input.Items
                .Select(x => x.CheckId).Distinct().Count() == input.Items.Length,
            "중복된 점검 항목이 있습니다."
        );
        var checks = await db.Set<WorkCheckItem>()
            .Where(x => x.OrderId == order.OrderId)
            .ToListAsync();
        foreach (var inputItem in input.Items)
        {
            var item = checks.SingleOrDefault(x => x.CheckId == ParseId(inputItem.CheckId));
            Require(item is not null, "다른 작업의 점검 항목입니다.");
            Require(
                inputItem.Comment is null || inputItem.Comment.Length <= 1000,
                "점검 메모는 1000자 이내입니다."
            );
            item!.Checked = inputItem.Checked;
            item.CheckedBy = item.Checked ? Actor(user) : null;
            item.CheckedAtUtc = item.Checked ? DateTime.UtcNow : null;
            item.CommentText = inputItem.Comment;
        }
        db.Entry(order).Property(x => x.UpdatedAtUtc).IsModified = true;
        Audit(
            order,
            user,
            "order.checklist",
            null
        );
        await db.SaveChangesAsync();
        var result = await ViewAsync(order);
        await tx.CommitAsync();
        log.LogInformation(
            "ChecklistAsync committed: order {OrderId}, actor {ActorId}, status {Status}, version {Version}",
            order.OrderId,
            Actor(user),
            order.Status,
            order.Version
        );
        return result;
    }

    private static string Hash(
        long order,
        long actor,
        EntryInput input
    )
    {
        var canonical = JsonSerializer.Serialize(
            new
            {
                order,
                actor,
                input.ClientEntryId,
                input.Kind,
                input.Qty,
                input.ReversalOf,
                input.CorrectionId,
                input.Reason,
                input.OccurredAt
            }
        );
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static EntryView Entry(ProductionEntry x) => new(
        Id(x.EntryId),
        Id(x.OrderId),
        x.ClientEntryId,
        x.Kind,
        x.Qty,
        x.ReversalOf is null ? null : Id(x.ReversalOf.Value),
        x.CorrectionId is null ? null : Id(x.CorrectionId.Value),
        x.Reason,
        x.OccurredAtUtc,
        Id(x.RecordedBy),
        x.Source,
        x.TelemetryId?.ToString()
    );

    private void Audit(
        WorkOrder order,
        ClaimsPrincipal user,
        string action,
        string? reason
    )
    {
        db.Add(
            new AuditEvent {
                ActorId = Actor(user),
                Principal = "USER:" + Id(Actor(user)),
                Action = action,
                EntityType = "WORK_ORDER",
                EntityId = Id(order.OrderId),
                Detail = JsonSerializer.Serialize(new
                {
                    order.Status,
                    reason
                }),
                OccurredAtUtc = DateTime.UtcNow
            }
        );
    }
}

