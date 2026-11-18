using System.Security.Claims;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Features.Orders;

public sealed partial class OrderService
{
    public async Task<EntryBatchResult> AppendAsync(
        string id,
        EntryBatch input,
        ClaimsPrincipal user
    )
    {
        // 작업자 실적 배치를 받아 UUID 멱등성과 작업 잠금을 확인하고 원본·역분개를 같은 원장에 기록한다.
        log.LogDebug("Order AppendAsync entered by actor {ActorId}", Actor(user));

        Require(input.Entries is { Length: >= 1 and <= 50 }, "실적은 한 번에 1~50개입니다.");
        Require(input.Entries.All(x => x is not null), "실적 항목이 비어 있습니다.");
        var now = DateTime.UtcNow;
        foreach (var item in input.Entries)
        {
            Require(Guid.TryParseExact(
                item.ClientEntryId,
                "D",
                out _
            ), "실적 UUID가 필요합니다.");
            Require(item.Kind is "TOTAL" or "REJECT" or "REVERSAL",
                "MANUAL에서는 TOTAL, REJECT, REVERSAL만 허용합니다.");
            Require(item.Qty is > 0 and <= MaxQty, "수량은 양의 정수 EA입니다.");
            Require((item.Kind == "REVERSAL") == (item.ReversalOf is not null),
                "역분개에만 원본 ID가 필요합니다.");
            Require(item.OccurredAt.Kind == DateTimeKind.Utc && item.OccurredAt <= now,
                "실적 시각은 현재 이전 UTC 시각이어야 합니다.");
            Require(item.Reason is null || item.Reason.Length <= 1000, "사유는 1000자 이내입니다.");
            Require(item.Kind == "TOTAL" || !string.IsNullOrWhiteSpace(item.Reason),
                "불량·역분개에는 사유가 필요합니다.");
        }
        var ids = input.Entries.Select(x => x.ClientEntryId).ToArray();
        Require(ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() == ids.Length,
            "배치 안에 같은 실적 UUID가 있습니다.");

        await using var transaction = await db.Database.BeginTransactionAsync();
        var order = await LoadAsync(
            ParseId(id),
            user,
            true
        );
        var actor = Actor(user);
        var hashes = input.Entries.Select(x => Hash(
            order.OrderId,
            actor,
            x
        )).ToArray();
        var stored = await db.Set<ProductionEntry>()
            .Where(x => ids.Contains(x.ClientEntryId))
            .ToListAsync();
        var duplicate = stored.Count > 0;
        log.LogDebug(
            "Ledger idempotency: order {OrderId}, duplicate {Duplicate}",
            order.OrderId,
            duplicate
        );
        if (duplicate)
        {
            if (stored.Count != ids.Length || input.Entries.Where((entry, i) =>
                !stored.Any(x => x.ClientEntryId == entry.ClientEntryId
                    && x.OrderId == order.OrderId && x.RequestHash == hashes[i])).Any())
            {
                log.LogWarning("Order AppendAsync rejected with code {Code}",
                    "IdempotencyConflict");
                throw new OrderFault(
                    409,
                    "IdempotencyConflict",
                    "실적 UUID의 내용이 다르거나 일부만 중복입니다."
                );
            }
        }
        else
        {
            Require(order.CountMode != "AUTO" || input.Entries.All(x => x.Kind != "TOTAL"),
                "AUTO 총수량은 설비가 기록합니다. 수동 TOTAL을 입력할 수 없습니다.");
            Version(order, input.ExpectedVersion);
            State(
                order,
                "RUNNING",
                "PAUSED",
                "SUBMITTED"
            );
            Require(input.Entries.All(x => x.OccurredAt >= order.StartedAtUtc),
                "작업 시작 이후의 실적만 입력해 주세요.");
            await ValidateCorrectionLinkAsync(order, input.Entries);

            var ledger = await db.Set<ProductionEntry>()
                .Where(x => x.OrderId == order.OrderId)
                .ToListAsync();
            var reversals = input.Entries.Where(x => x.Kind == "REVERSAL").ToArray();
            var originalIds = reversals.Select(x => ParseId(x.ReversalOf)).ToArray();
            if (originalIds.Distinct().Count() != originalIds.Length)
            {
                log.LogWarning("Order AppendAsync rejected with code {Code}", "ReversalConflict");
                throw new OrderFault(
                    409,
                    "ReversalConflict",
                    "같은 원본을 두 번 역분개할 수 없습니다."
                );
            }

            var totals = await TotalsAsync(order.OrderId);
            var total = totals.Total;
            var reject = totals.Reject;
            foreach (var item in input.Entries)
            {
                var kind = item.Kind;
                var sign = 1;
                if (kind == "REVERSAL")
                {
                    var originalId = ParseId(item.ReversalOf);
                    var original = ledger.SingleOrDefault(x => x.EntryId == originalId);
                    Require(original is not null, "같은 작업의 원본만 역분개할 수 있습니다.");
                    Require(original.Source == "MANUAL", "설비 자동 원장은 수동 역분개할 수 없습니다.");
                    Require(original.Kind is "TOTAL" or "REJECT", "역분개의 역분개는 금지합니다.");
                    Require(original.Qty == item.Qty, "역분개 수량은 원본과 같아야 합니다.");
                    if (order.Status is "RUNNING" or "PAUSED")
                    {
                        Require(original.RecordedBy == actor && original.CorrectionId is null,
                            "제출 전에는 본인이 입력한 일반 원본만 취소할 수 있습니다.");
                    }
                    if (ledger.Any(x => x.ReversalOf == originalId))
                    {
                        log.LogWarning("Order AppendAsync rejected with code {Code}",
                            "ReversalConflict");
                        throw new OrderFault(
                            409,
                            "ReversalConflict",
                            "이미 역분개된 원본입니다."
                        );
                    }
                    kind = original.Kind;
                    sign = -1;
                }

                if (kind == "REJECT")
                {
                    reject += sign * item.Qty;
                }
                else
                {
                    total += sign * item.Qty;
                }
            }
            Require(total is >= 0 and <= MaxQty && reject >= 0 && reject <= total,
                "합계 범위 또는 불량 수량을 확인해 주세요.");

            log.LogDebug(
                "Manual ledger append Order={OrderId} Actor={ActorId} Entries={Count} Mode={Mode}",
                order.OrderId,
                actor,
                input.Entries.Length,
                order.CountMode
            );
            stored = input.Entries.Select((x, i) => new ProductionEntry
            {
                ClientEntryId = x.ClientEntryId,
                OrderId = order.OrderId,
                Kind = x.Kind,
                Qty = x.Qty,
                ReversalOf = x.ReversalOf is null ? null : ParseId(x.ReversalOf),
                CorrectionId = x.CorrectionId is null ? null : ParseId(x.CorrectionId),
                Reason = x.Reason,
                RecordedBy = actor,
                OccurredAtUtc = x.OccurredAt,
                RequestHash = hashes[i]
            }).ToList();
            db.AddRange(stored);
            db.Entry(order).Property(x => x.UpdatedAtUtc).IsModified = true;
            Audit(
                order,
                user,
                "order.entries",
                null
            );
            await db.SaveChangesAsync();
        }

        // Read a coherent response before releasing the parent lock.
        var final = await TotalsAsync(order.OrderId);
        var result = new EntryBatchResult(
            Id(order.OrderId),
            order.Version,
            duplicate,
            stored.Select(Entry).ToArray(),
            final.Total,
            final.Reject
        );
        await transaction.CommitAsync();
        if (!duplicate)
        {
            log.LogInformation(
                "Ledger append committed: order {OrderId}, actor {ActorId}, entries {Count}, mode {Mode}",
                order.OrderId,
                actor,
                stored.Count,
                order.CountMode
            );
        }
        return result;
    }

    private async Task ValidateCorrectionLinkAsync(WorkOrder order, EntryInput[] entries)
    {
        // 실적 저장 전에 제출 전 일반 입력과 제출 후 관리자 정정 연결을 구분해 검증한다.
        log.LogDebug("Order ValidateCorrectionLinkAsync entered");

        if (order.Status != "SUBMITTED")
        {
            Require(entries.All(x => x.CorrectionId is null),
                "제출 전 실적에는 관리자 정정 요청을 연결할 수 없습니다.");
            return;
        }

        var open = await db.Set<CorrectionRequest>()
            .SingleOrDefaultAsync(x => x.OrderId == order.OrderId && x.Status == "OPEN");
        Require(open is not null, "제출 후에는 관리자의 열린 정정 요청이 필요합니다.");
        Require(entries.All(x => x.CorrectionId is not null
            && ParseId(x.CorrectionId) == open.CorrectionId),
            "모든 정정 실적은 같은 작업의 열린 정정 요청에 연결해야 합니다.");
        Require(entries.All(x => !string.IsNullOrWhiteSpace(x.Reason)),
            "정정 실적에는 사유가 필요합니다.");
    }
}



