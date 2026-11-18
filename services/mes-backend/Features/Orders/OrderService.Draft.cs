using System.Security.Claims;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Features.Orders;

public sealed partial class OrderService
{
    public async Task<OrderView> UpdateDraftAsync(
        string id,
        OrderDraftUpdate input,
        ClaimsPrincipal user
    )
    {
        // 관리자 초안 변경을 받아 활성 참조·AUTO 매핑을 재검증하고 점검 항목을 새 정의로 교체한다.
        log.LogDebug("Order UpdateDraftAsync entered by actor {ActorId}", Actor(user));

        Require(input.CountMode is "MANUAL" or "AUTO", "작업 집계 방식을 확인하세요.");
        Require(input.TargetQty is > 0 and <= MaxQty, "목표 수량 범위를 확인해 주세요.");
        Require(input.PlannedStart.Kind == DateTimeKind.Utc
            && input.PlannedEnd.Kind == DateTimeKind.Utc
            && input.PlannedEnd > input.PlannedStart, "UTC 계획 시간을 확인해 주세요.");
        Require(input.OperationLabel is null || input.OperationLabel.Length <= 100,
            "작업명은 100자 이내입니다.");
        Require(input.Checklist is not null && input.Checklist.Length <= 30,
            "점검표는 30개 이내입니다.");
        Require(input.Checklist!.All(x => x is not null
            && x.Seq is >= 1 and <= 100
            && !string.IsNullOrWhiteSpace(x.Label) && x.Label.Length <= 200)
            && input.Checklist.Select(x => x.Seq).Distinct().Count() == input.Checklist.Length,
            "점검표 순번과 이름을 확인해 주세요.");
        var product = ParseId(input.ProductId);
        var equipment = ParseId(input.EquipmentId);
        var assignee = ParseId(input.AssigneeId);

        await using var tx = await db.Database.BeginTransactionAsync();
        var order = await LoadAsync(
            ParseId(id),
            user,
            true
        );
        Version(order, input.ExpectedVersion);
        State(order, "DRAFT");
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
        order.CountMode = input.CountMode;
        order.CounterMappingId = mapping;
        order.ProductId = product;
        order.EquipmentId = equipment;
        order.AssigneeId = assignee;
        order.OperationLabel = input.OperationLabel;
        order.TargetQty = input.TargetQty;
        order.PlannedStartUtc = input.PlannedStart;
        order.PlannedEndUtc = input.PlannedEnd;
        db.Entry(order).Property(x => x.UpdatedAtUtc).IsModified = true;
        var oldItems = await db.Set<WorkCheckItem>()
            .Where(x => x.OrderId == order.OrderId)
            .ToListAsync();
        db.RemoveRange(oldItems);
        // Delete first so unchanged sequence numbers cannot collide with their replacements.
        await db.SaveChangesAsync();
        foreach (var item in input.Checklist)
        {
            db.Add(new WorkCheckItem
            {
                OrderId = order.OrderId,
                Seq = item.Seq,
                Label = item.Label,
                Required = item.Required
            });
        }
        Audit(
            order,
            user,
            "order.draft.updated",
            null
        );
        await db.SaveChangesAsync();
        var result = await ViewAsync(order);
        await tx.CommitAsync();
        log.LogInformation(
            "UpdateDraftAsync committed: order {OrderId}, actor {ActorId}, status {Status}, version {Version}",
            order.OrderId,
            Actor(user),
            order.Status,
            order.Version
        );
        return result;
    }

    public async Task<OrderView> CancelAsync(
        string id,
        ReasonCommand input,
        ClaimsPrincipal user
    )
    {
        // 관리자 취소 요청을 받아 착수·실적이 없는 초안/발행 작업만 취소하고 이력을 남긴다.
        log.LogDebug("Order CancelAsync entered by actor {ActorId}", Actor(user));

        Require(!string.IsNullOrWhiteSpace(input.Reason) && input.Reason.Length <= 1000,
            "취소 사유는 1~1000자입니다.");
        await using var tx = await db.Database.BeginTransactionAsync();
        var order = await LoadAsync(
            ParseId(id),
            user,
            true
        );
        Version(order, input.ExpectedVersion);
        State(
            order,
            "DRAFT",
            "RELEASED"
        );
        Require(order.StartedAtUtc is null
            && !await db.Set<ProductionEntry>().AnyAsync(x => x.OrderId == order.OrderId),
            "착수 이력이 있는 작업은 취소할 수 없습니다.");
        var previous = order.Status;
        var now = DateTime.UtcNow;
        order.Status = "CANCELLED";
        order.CancelReason = input.Reason;
        order.CancelledAtUtc = now;
        db.Add(new OrderTransition
        {
            OrderId = order.OrderId,
            FromStatus = previous,
            ToStatus = "CANCELLED",
            ActorId = Actor(user),
            Principal = "USER:" + Id(Actor(user)),
            Reason = input.Reason,
            OccurredAtUtc = now
        });
        Audit(
            order,
            user,
            "order.cancel",
            input.Reason
        );
        await db.SaveChangesAsync();
        var result = await ViewAsync(order);
        await tx.CommitAsync();
        log.LogInformation(
            "CancelAsync committed: order {OrderId}, actor {ActorId}, status {Status}, version {Version}",
            order.OrderId,
            Actor(user),
            order.Status,
            order.Version
        );
        return result;
    }
}


