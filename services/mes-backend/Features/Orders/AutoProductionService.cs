using FactoryMES.Backend.Persistence;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Features.Orders;

// CollectionService의 검증된 수집 이벤트를 받아 AUTO 원장으로 바꾸는 소비기다.
// 호출자는 먼저 설비 행을 잠그고 Oracle 트랜잭션을 시작한다. 외부 통신은 이 클래스에서 하지 않는다.
public sealed partial class AutoProductionService(MesDbContext db,
    ILogger<AutoProductionService> log)
{
    private const long MaxQty = 999999999999;

    // 수집/작업 명령 모두 Equipment→Target/Order 순서로 잠가 작업 전환 경계를 일치시킨다.
    public static async Task LockEquipmentAsync(MesDbContext db, long equipmentId)
    {
        await db.Set<Equipment>().FromSqlInterpolated(
            $"SELECT * FROM EQUIPMENT WHERE EQUIPMENT_ID = {equipmentId} FOR UPDATE"
        ).ToListAsync();
    }

    private sealed record Binding(
        EquipmentTagMapping Mapping,
        IntegrationTarget Target,
        EquipmentConnection Connection
    );

    private async Task<Binding> BindingAsync(long equipment, long mapping)
    {
        var result = await (
            from m in db.Set<EquipmentTagMapping>()
            join t in db.Set<IntegrationTarget>() on m.TargetId equals t.TargetId
            join c in db.Set<EquipmentConnection>() on t.TargetId equals c.TargetId
            where m.MappingId == mapping && t.EquipmentId == equipment
                && m.Enabled == EF.Parameter(true) && t.Enabled == EF.Parameter(true)
                && t.Deleted == EF.Parameter(false) && t.Kind == "EQUIPMENT"
                && m.ValueKind == "COUNTER" && m.Unit == "EA" && m.Scale == 1 && m.OffsetValue == 0
            select new {
                m,
                t,
                c
            }
        ).SingleOrDefaultAsync();
        if (result is null)
        {
            log.LogWarning(
                "AUTO mapping rejected Equipment={EquipmentId} Mapping={MappingId}",
                equipment,
                mapping
            );
            throw new OrderFault(
                422,
                "AutoMappingInvalid",
                "같은 설비의 활성 EA 카운터(배율1·오프셋0)를 선택하세요."
            );
        }
        return new Binding(
            result.m,
            result.t,
            result.c
        );
    }

    public async Task ValidateMappingAsync(
        long equipment,
        string mode,
        long? mapping
    )
    {
        if (mode == "MANUAL" && mapping is null) return;
        if (mode != "AUTO" || mapping is null)
            throw new OrderFault(
                422,
                "CountModeInvalid",
                "MANUAL은 매핑 없이, AUTO는 카운터 매핑이 필요합니다."
            );
        await BindingAsync(equipment, mapping.Value);
        log.LogDebug(
            "AUTO mapping validated Equipment={EquipmentId} Mapping={MappingId}",
            equipment,
            mapping
        );
    }

    public async Task<AutoCounterOption[]> OptionsAsync(long equipment)
    {
        var rows = await (
            from m in db.Set<EquipmentTagMapping>()
            join t in db.Set<IntegrationTarget>() on m.TargetId equals t.TargetId
            join c in db.Set<EquipmentConnection>() on t.TargetId equals c.TargetId
            where t.EquipmentId == equipment && t.Kind == "EQUIPMENT"
                && t.Enabled == EF.Parameter(true) && t.Deleted == EF.Parameter(false)
                && m.Enabled == EF.Parameter(true) && m.ValueKind == "COUNTER"
                && m.Unit == "EA" && m.Scale == 1 && m.OffsetValue == 0
            orderby m.MappingId
            select new {
                m,
                t,
                c
            }
        ).ToArrayAsync();
        log.LogDebug(
            "AUTO options Equipment={EquipmentId} Count={Count}",
            equipment,
            rows.Length
        );
        return rows.Select(x => new AutoCounterOption(
            OrderService.Id(x.m.MappingId),
            OrderService.Id(equipment),
            x.t.TargetCode,
            x.m.MetricCode,
            x.m.Unit,
            x.t.ConfigVersion,
            x.m.LatestValue is { } value ? CollectionFormat.Number(value) : null,
            x.m.LatestSampleAtUtc,
            x.m.LatestReceivedAtUtc,
            x.c.StaleSec
        )).ToArray();
    }

    private async Task<TelemetryEvent> FreshReadingAsync(WorkOrder order, Binding binding)
    {
        var latest = await (
            from e in db.Set<TelemetryEvent>()
            join m in db.Set<TelemetryMetric>() on e.TelemetryId equals m.TelemetryId
            where m.MappingId == order.CounterMappingId && e.ConfigVersion == binding.Target
                .ConfigVersion
                && e.TargetId == binding.Target.TargetId
            orderby e.OccurredAtUtc descending, e.TelemetryId descending
            select new {
                e,
                m
            }
        ).FirstOrDefaultAsync();
        var cutoff = CollectionFormat.UtcNow().AddSeconds(-binding.Connection.StaleSec);
        if (latest is null || latest.m.Quality != "GOOD" || latest.m.NumericValue is null
            || latest.e.OccurredAtUtc < cutoff || latest.e.ReceivedAtUtc < cutoff)
        {
            log.LogWarning(
                "AUTO fresh sample missing Order={OrderId} Mapping={MappingId}",
                order.OrderId,
                order.CounterMappingId
            );
            throw new OrderFault(
                409,
                "AutoCounterStale",
                "현재 설정의 신선한 GOOD 카운터 수집을 확인하세요."
            );
        }
        return latest.e;
    }

    // 시작·재개·리셋 확인은 미래 첫 샘플을 기준으로 잡는다. 이전 작업/정지 구간 수량을 넘겨받지 않는다.
    public async Task ArmAsync(WorkOrder order, DateTime now)
    {
        if (order.CountMode != "AUTO") return;
        var binding = await BindingAsync(order.EquipmentId, order.CounterMappingId!.Value);
        await FreshReadingAsync(order, binding);
        var state = await db.Set<AutoCounterState>().SingleOrDefaultAsync(x => x.OrderId == order
            .OrderId);
        if (state is null)
        {
            state = new AutoCounterState {
                OrderId = order.OrderId
            };
            db.Add(state);
        }
        state.AwaitingBaseline = true;
        state.AcceptAfterUtc = now;
        state.ConfigVersion = binding.Target.ConfigVersion;
        state.LastTelemetryId = null;
        state.LastCounter = null;
        state.LastSampleAtUtc = null;
        state.LastReceivedAtUtc = null;
        state.LastStreamId = null;
        state.LastSequenceNo = null;
        log.LogInformation(
            "AUTO baseline armed Order={OrderId} Equipment={EquipmentId} After={AcceptAfter}",
            order.OrderId,
            order.EquipmentId,
            now
        );
    }

    public async Task RequireReadyAsync(WorkOrder order)
    {
        if (order.CountMode != "AUTO") return;
        var state = await db.Set<AutoCounterState>().SingleOrDefaultAsync(x => x.OrderId == order
            .OrderId);
        if (order.CounterInvalid || state is null || state.AwaitingBaseline)
            throw new OrderFault(
                409,
                "AutoCounterNotReady",
                "카운터 이상을 확인하고 자동 기준값 확정을 기다리세요."
            );
        var binding = await BindingAsync(order.EquipmentId, order.CounterMappingId!.Value);
        var latest = await FreshReadingAsync(order, binding);
        if (state.LastTelemetryId != latest.TelemetryId)
            throw new OrderFault(
                409,
                "AutoCounterNotReady",
                "최신 계측 처리가 끝난 뒤 다시 제출하세요."
            );
        order.EndCounter = state.LastCounter;
        order.EndSampleAtUtc = state.LastSampleAtUtc;
        order.EndReceivedAtUtc = state.LastReceivedAtUtc;
    }

    public async Task<AutoCounterView?> ViewAsync(WorkOrder order)
    {
        if (order.CountMode != "AUTO") return null;
        var state = await db.Set<AutoCounterState>().SingleOrDefaultAsync(x => x.OrderId == order
            .OrderId);
        return state is null ? null : new AutoCounterView(
            state.AwaitingBaseline,
            state.LastCounter is { } value ? CollectionFormat.Number(value) : null,
            state.LastStreamId,
            state.LastSequenceNo?.ToString(),
            state.LastTelemetryId?.ToString(),
            state.LastSampleAtUtc,
            state.AcceptAfterUtc,
            state.ConfigVersion
        );
    }
}

