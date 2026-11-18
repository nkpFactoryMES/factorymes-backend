using System.Security.Cryptography;
using System.Text;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Features.Orders;

public sealed partial class AutoProductionService
{
    // 검증·저장한 원시 이벤트만 받는다. 행/원장/체크포인트는 수집 트랜잭션과 함께 커밋·롤백한다.
    public async Task ConsumeAsync(TelemetryEvent sample)
    {
        var orders = await db.Set<WorkOrder>().FromSqlInterpolated($"""
            SELECT * FROM WORK_ORDER
            WHERE EQUIPMENT_ID = {sample.EquipmentId} AND COUNT_MODE = 'AUTO'
                AND STATUS IN ('RUNNING','PAUSED') FOR UPDATE
            """).ToListAsync();
        if (orders.Count == 0)
        {
            log.LogDebug(
                "AUTO sample has no active order Equipment={EquipmentId} Telemetry={TelemetryId}",
                sample.EquipmentId,
                sample.TelemetryId
            );
            return;
        }
        if (orders.Count != 1)
        {
            // 비정상 기존 데이터가 원시 계측 전체를 막지 않도록 생산 투영만 중단한다.
            log.LogWarning(
                "AUTO ambiguous active orders Equipment={EquipmentId} Count={Count}",
                sample.EquipmentId,
                orders.Count
            );
            return;
        }
        var order = orders[0];
        var state = await db.Set<AutoCounterState>()
            .SingleOrDefaultAsync(x => x.OrderId == order.OrderId);
        if (state is null)
        {
            // 이전/수동 생성 데이터에 상태가 없으면 과거 누적값을 추정하지 않는다.
            // 원시 수집은 보존하고 이상 표시 후 기존 사유 필수 재기준 명령으로 복구한다.
            if (!order.CounterInvalid)
            {
                order.CounterInvalid = true;
                await db.SaveChangesAsync();
            }
            log.LogWarning("AUTO checkpoint missing Order={OrderId}", order.OrderId);
            return;
        }
        var metric = await db.Set<TelemetryMetric>().SingleOrDefaultAsync(x =>
            x.TelemetryId == sample.TelemetryId && x.MappingId == order.CounterMappingId);
        log.LogDebug(
            "AUTO sample received Order={OrderId} Telemetry={TelemetryId} " +
                "Stream={StreamId} Sequence={Sequence}",
            order.OrderId,
            sample.TelemetryId,
            sample.StreamId,
            sample.SequenceNo
        );
        if (sample.ConfigVersion != state.ConfigVersion || metric is null)
        {
            log.LogDebug(
                "AUTO configuration/mapping skipped Order={OrderId} Telemetry={TelemetryId}",
                order.OrderId,
                sample.TelemetryId
            );
            return;
        }
        if (metric.Quality != "GOOD" || metric.NumericValue is not { } raw)
        {
            log.LogWarning(
                "AUTO sample quality skipped Order={OrderId} " +
                    "Telemetry={TelemetryId} Quality={Quality}",
                order.OrderId,
                sample.TelemetryId,
                metric.Quality
            );
            return;
        }
        if (sample.ReceivedAtUtc < state.AcceptAfterUtc
            || sample.OccurredAtUtc < state.AcceptAfterUtc
            || (state.LastSampleAtUtc is { } last && sample.OccurredAtUtc < last)
            || (state.LastStreamId == sample.StreamId && state.LastSequenceNo >= sample.SequenceNo)
            || (state.LastStreamId is not null && state.LastStreamId != sample.StreamId
                && state.LastSampleAtUtc == sample.OccurredAtUtc))
        {
            log.LogWarning(
                "AUTO old/ambiguous sample ignored Order={OrderId} " +
                    "Telemetry={TelemetryId} Sequence={Sequence}",
                order.OrderId,
                sample.TelemetryId,
                sample.SequenceNo
            );
            return;
        }
        var wasInvalid = order.CounterInvalid;
        var previous = state.LastCounter;
        var baseline = state.AwaitingBaseline;
        var reset = !baseline && previous is { } prior && raw < prior;
        var delta = baseline || previous is null ? 0 : raw - previous.Value;
        if (state.LastStreamId is not null && state.LastStreamId != sample.StreamId)
            log.LogInformation(
                "AUTO stream changed Order={OrderId} Telemetry={TelemetryId}; " +
                    "cumulative continuity checked",
                order.OrderId,
                sample.TelemetryId
            );
        if (state.LastStreamId == sample.StreamId && state.LastSequenceNo is { } seq
            && sample.SequenceNo > seq + 1)
            log.LogWarning(
                "AUTO sequence gap covered by cumulative counter Order={OrderId} " +
                    "Previous={Previous} Current={Current}",
                order.OrderId,
                seq,
                sample.SequenceNo
            );
        // 검증된 최신 수신 위치를 먼저 기억한다. PAUSED/이상 상태에서도 수집 진행은 기록하지만 생산량은 더하지 않는다.
        state.LastTelemetryId = sample.TelemetryId;
        state.LastCounter = raw;
        state.LastSampleAtUtc = sample.OccurredAtUtc;
        state.LastReceivedAtUtc = sample.ReceivedAtUtc;
        state.LastStreamId = sample.StreamId;
        state.LastSequenceNo = sample.SequenceNo;
        state.AwaitingBaseline = false;
        if (baseline)
        {
            if (order.StartCounter is null)
            {
                order.StartCounter = raw;
                order.StartSampleAtUtc = sample.OccurredAtUtc;
                order.StartReceivedAtUtc = sample.ReceivedAtUtc;
            }
            SetEnd(
                order,
                sample,
                raw
            );
            log.LogInformation(
                "AUTO baseline staged Order={OrderId} Telemetry={TelemetryId} Counter={Counter}",
                order.OrderId,
                sample.TelemetryId,
                raw
            );
        }
        else if (reset)
        {
            order.CounterInvalid = true;
            SetEnd(
                order,
                sample,
                raw
            );
            db.Add(new AuditEvent
            {
                Principal = "SYSTEM:auto-counter",
                Action = "order.counter.reset",
                EntityType = "WORK_ORDER",
                EntityId = order.OrderId.ToString(),
                Detail = CollectionFormat.Canonical(new {
                    previous,
                    current = raw,
                    sample.TelemetryId
                }),
                OccurredAtUtc = CollectionFormat.UtcNow()
            });
            log.LogWarning(
                "AUTO counter decreased; confirmation required Order={OrderId} " +
                    "Previous={Previous} Current={Current}",
                order.OrderId,
                previous,
                raw
            );
        }
        else if (order.Status == "PAUSED" || order.CounterInvalid || delta == 0)
        {
            log.LogDebug(
                "AUTO no production Order={OrderId} Status={Status} " +
                    "Invalid={Invalid} Delta={Delta}",
                order.OrderId,
                order.Status,
                order.CounterInvalid,
                delta
            );
        }
        else
        {
            var total = await db.Set<ProductionEntry>()
                .Where(x => x.OrderId == order.OrderId && x.Kind == "TOTAL")
                .SumAsync(x => (long?)x.Qty) ?? 0;
            if (delta < 0 || decimal.Truncate(delta) != delta || delta > MaxQty
                || total + delta > MaxQty)
            {
                order.CounterInvalid = true;
                db.Add(new AuditEvent
                {
                    Principal = "SYSTEM:auto-counter",
                    Action = "order.counter.range_rejected",
                    EntityType = "WORK_ORDER",
                    EntityId = order.OrderId.ToString(),
                    Detail = CollectionFormat.Canonical(new { delta, total, sample.TelemetryId }),
                    OccurredAtUtc = CollectionFormat.UtcNow()
                });
                log.LogWarning(
                    "AUTO quantity range rejected Order={OrderId} Delta={Delta} Total={Total}",
                    order.OrderId,
                    delta,
                    total
                );
            }
            else
            {
                var identity = Encoding.UTF8
                    .GetBytes($"AUTO:{order.OrderId}:{sample.TelemetryId}:{order.CounterMappingId}");
                var entryId = new Guid(SHA256.HashData(identity).AsSpan(0, 16)).ToString();
                db.Add(new ProductionEntry
                {
                    ClientEntryId = entryId,
                    OrderId = order.OrderId,
                    Kind = "TOTAL",
                    Qty = (long)delta,
                    Source = "AUTO",
                    TelemetryId = sample.TelemetryId,
                    RecordedBy = order.AssigneeId,
                    OccurredAtUtc = sample.OccurredAtUtc,
                    Reason = "설비 카운터 증분",
                    RequestHash = CollectionFormat.Hash(new {
                        order.OrderId,
                        sample.TelemetryId,
                        delta
                    })
                });
                SetEnd(
                    order,
                    sample,
                    raw
                );
                log.LogDebug(
                    "AUTO production staged Order={OrderId} " +
                        "Equipment={EquipmentId} Telemetry={TelemetryId} Qty={Qty}",
                    order.OrderId,
                    order.EquipmentId,
                    sample.TelemetryId,
                    delta
                );
            }
        }
        // 정상 누적은 편집 충돌을 만들지 않는다. 리셋/범위 이상 전이는 명령과 충돌시켜 재확인한다.
        await db.SaveCounterProjectionAsync(
            preserveOrderVersion: !reset && wasInvalid == order.CounterInvalid
        );
    }

    private static void SetEnd(
        WorkOrder order,
        TelemetryEvent sample,
        decimal raw
    )
    {
        order.EndCounter = raw;
        order.EndSampleAtUtc = sample.OccurredAtUtc;
        order.EndReceivedAtUtc = sample.ReceivedAtUtc;
    }
}

