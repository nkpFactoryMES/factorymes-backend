using System.Security.Claims;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Features.Orders;

public sealed partial class OrderService
{
    public async Task<OrderView> PauseAsync(
        string id,
        PauseRequest input,
        ClaimsPrincipal user
    )
    {
        // 작업자 중지 명령을 받아 열린 중지의 중복을 차단하고 작업 상태와 중지 이력을 함께 저장한다.
        log.LogDebug("Order PauseAsync entered by actor {ActorId}", Actor(user));

        Require(!string.IsNullOrWhiteSpace(input.ReasonCode) && input.ReasonCode.Length <= 40,
            "중지 사유 코드는 1~40자입니다.");
        Require(input.Note is null || input.Note.Length <= 1000, "중지 메모는 1000자 이내입니다.");
        Require(!string.Equals(
            input.ReasonCode.Trim(),
            "OTHER",
            StringComparison.OrdinalIgnoreCase
        )
            || !string.IsNullOrWhiteSpace(input.Note), "기타 중지 사유에는 메모가 필요합니다.");

        await using var transaction = await db.Database.BeginTransactionAsync();
        var order = await LoadAsync(
            ParseId(id),
            user,
            true
        );
        Version(order, input.ExpectedVersion);
        State(order, "RUNNING");
        Require(!await db.Set<WorkPause>().AnyAsync(x => x.OrderId == order.OrderId
            && x.EndedAtUtc == null), "이미 열린 중지가 있습니다.");
        long? alarmId = input.AlarmId is null ? null : ParseId(input.AlarmId);
        if (alarmId is not null)
        {
            Require(await db.Set<Alarm>().AnyAsync(x => x.AlarmId == alarmId
                && x.EquipmentId == order.EquipmentId), "같은 설비의 경보만 연결할 수 있습니다.");
        }

        var now = LifecycleTime(order);
        db.Add(new WorkPause
        {
            OrderId = order.OrderId,
            EquipmentId = order.EquipmentId,
            AlarmId = alarmId,
            ReasonCode = input.ReasonCode.Trim(),
            Note = input.Note,
            StartedAtUtc = now,
            RecordedBy = Actor(user)
        });
        order.Status = "PAUSED";
        RecordLifecycleTransition(
            order,
            "RUNNING",
            user,
            input.Note ?? input.ReasonCode,
            now
        );
        Audit(
            order,
            user,
            "order.pause",
            input.Note ?? input.ReasonCode
        );
        await db.SaveChangesAsync();
        var result = await ViewAsync(order);
        await transaction.CommitAsync();
        log.LogInformation(
            "PauseAsync committed: order {OrderId}, actor {ActorId}, status {Status}, version {Version}",
            order.OrderId,
            Actor(user),
            order.Status,
            order.Version
        );
        return result;
    }

    public async Task<OrderView> ResumeAsync(
        string id,
        Command input,
        ClaimsPrincipal user
    )
    {
        // 작업자 재개 명령을 받아 열린 중지를 닫고 AUTO는 다음 유효 샘플부터 다시 기준을 잡는다.
        log.LogDebug("Order ResumeAsync entered by actor {ActorId}", Actor(user));

        await using var transaction = await db.Database.BeginTransactionAsync();
        var order = await LoadAsync(
            ParseId(id),
            user,
            true
        );
        Version(order, input.ExpectedVersion);
        State(order, "PAUSED");
        var pauses = await db.Set<WorkPause>()
            .Where(x => x.OrderId == order.OrderId && x.EndedAtUtc == null)
            .ToListAsync();
        Require(pauses.Count == 1, "작업에는 열린 중지가 정확히 하나 있어야 합니다.");

        var now = LifecycleTime(order, pauses[0].StartedAtUtc);
        pauses[0].EndedAtUtc = now;
        if (order.CountMode == "AUTO")
        {
            Require(!order.CounterInvalid, "카운터 이상을 먼저 확인해 주세요.");
            await auto.ArmAsync(order, now);
        }
        order.Status = "RUNNING";
        RecordLifecycleTransition(
            order,
            "PAUSED",
            user,
            null,
            now
        );
        Audit(
            order,
            user,
            "order.resume",
            null
        );
        await db.SaveChangesAsync();
        var result = await ViewAsync(order);
        await transaction.CommitAsync();
        log.LogInformation(
            "ResumeAsync committed: order {OrderId}, actor {ActorId}, status {Status}, version {Version}",
            order.OrderId,
            Actor(user),
            order.Status,
            order.Version
        );
        return result;
    }

    public async Task<PageResult<CorrectionView>> CorrectionsAsync(
        string id,
        ClaimsPrincipal user,
        int? limit,
        string? cursor
    )
    {
        // 관리자·작업자 조회 요청을 받아 작업 접근 범위에 속한 정정 이력만 커서로 조회한다.
        log.LogDebug("Order CorrectionsAsync entered by actor {ActorId}", Actor(user));

        var order = await LoadAsync(
            ParseId(id),
            user,
            false
        );
        var size = limit ?? 50;
        Require(size is >= 1 and <= 100, "목록 크기는 1~100입니다.");
        var query = db.Set<CorrectionRequest>().Where(x => x.OrderId == order.OrderId);
        if (cursor is not null)
        {
            var after = ParseId(cursor);
            query = query.Where(x => x.CorrectionId < after);
        }

        var rows = await query.OrderByDescending(x => x.CorrectionId)
            .Take(size + 1)
            .ToListAsync();
        return new PageResult<CorrectionView>(
            rows.Take(size).Select(x => Correction(x, order.Version)).ToArray(),
            rows.Count > size ? Id(rows[size - 1].CorrectionId) : null
        );
    }

    public async Task<CorrectionView> OpenCorrectionAsync(
        string id,
        CorrectionOpen input,
        ClaimsPrincipal user
    )
    {
        // 제출된 작업에 대한 관리자 요청을 받아 열린 정정 중복을 막고 정정 요청을 저장한다.
        log.LogDebug("Order OpenCorrectionAsync entered by actor {ActorId}", Actor(user));

        Require(!string.IsNullOrWhiteSpace(input.Reason) && input.Reason.Length <= 1000,
            "정정 요청 사유는 1~1000자입니다.");
        await using var transaction = await db.Database.BeginTransactionAsync();
        var order = await LoadAsync(
            ParseId(id),
            user,
            true
        );
        Version(order, input.ExpectedVersion);
        State(order, "SUBMITTED");
        if (await db.Set<CorrectionRequest>().AnyAsync(x => x.OrderId == order.OrderId
            && x.Status == "OPEN"))
        {
            log.LogWarning("Order OpenCorrectionAsync rejected with code {Code}",
                "CorrectionAlreadyOpen");
            throw new OrderFault(
                409,
                "CorrectionAlreadyOpen",
                "이미 열린 정정 요청이 있습니다."
            );
        }

        var correction = new CorrectionRequest
        {
            OrderId = order.OrderId,
            Status = "OPEN",
            Reason = input.Reason,
            RequestedBy = Actor(user)
        };
        db.Add(correction);
        db.Entry(order).Property(x => x.UpdatedAtUtc).IsModified = true;
        Audit(
            order,
            user,
            "order.correction.open",
            input.Reason
        );
        await db.SaveChangesAsync();
        var result = Correction(correction, order.Version);
        await transaction.CommitAsync();
        log.LogInformation(
            "OpenCorrectionAsync committed: order {OrderId}, actor {ActorId}, status {Status}, version {Version}",
            order.OrderId,
            Actor(user),
            order.Status,
            order.Version
        );
        return result;
    }

    public async Task<CorrectionView> ResolveCorrectionAsync(
        string id,
        CorrectionResolve input,
        ClaimsPrincipal user
    )
    {
        // 관리자 정정 확인을 받아 부모 작업을 먼저 잠그고 최신 실적 합계·정정 버전을 검증해 종결한다.
        log.LogDebug("Order ResolveCorrectionAsync entered by actor {ActorId}", Actor(user));

        Require(!string.IsNullOrWhiteSpace(input.ResolutionNote)
            && input.ResolutionNote.Length <= 1000, "정정 확인 사유는 1~1000자입니다.");
        var correctionId = ParseId(id);
        var header = await db.Set<CorrectionRequest>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.CorrectionId == correctionId)
            ?? throw new OrderFault(
                404,
                "CorrectionNotFound",
                "정정 요청을 찾을 수 없습니다."
            );

        // Always lock parent first, like entry writes and order closing.
        await using var transaction = await db.Database.BeginTransactionAsync();
        var order = await LoadAsync(
            header.OrderId,
            user,
            true
        );
        Version(order, input.ExpectedOrderVersion);
        State(order, "SUBMITTED");
        var rows = await db.Set<CorrectionRequest>()
            .FromSqlInterpolated(
                $"SELECT * FROM CORRECTION_REQUEST WHERE CORRECTION_ID = {correctionId} FOR UPDATE"
            )
            .ToListAsync();
        var correction = rows.SingleOrDefault()
            ?? throw new OrderFault(
                404,
                "CorrectionNotFound",
                "정정 요청을 찾을 수 없습니다."
            );
        if (input.ExpectedVersion <= 0 || correction.Version != input.ExpectedVersion
            || correction.Status != "OPEN")
        {
            log.LogWarning("Order ResolveCorrectionAsync rejected with code {Code}",
                "CorrectionConflict");
            throw new OrderFault(
                409,
                "CorrectionConflict",
                "정정 상태 또는 버전이 변경됐습니다."
            );
        }

        var totals = await TotalsAsync(order.OrderId);
        Require(totals.Total >= totals.Reject && totals.Reject >= 0, "실적 합계를 확인해 주세요.");
        correction.Status = "RESOLVED";
        correction.ResolvedBy = Actor(user);
        correction.ResolvedAtUtc = LifecycleTime(order, correction.CreatedAtUtc);
        correction.ResolutionNote = input.ResolutionNote;
        db.Entry(order).Property(x => x.UpdatedAtUtc).IsModified = true;
        Audit(
            order,
            user,
            "order.correction.resolve",
            input.ResolutionNote
        );
        await db.SaveChangesAsync();
        var result = Correction(correction, order.Version);
        await transaction.CommitAsync();
        log.LogInformation(
            "ResolveCorrectionAsync committed: order {OrderId}, actor {ActorId}, status {Status}, version {Version}",
            order.OrderId,
            Actor(user),
            order.Status,
            order.Version
        );
        return result;
    }

    private static CorrectionView Correction(CorrectionRequest item, long orderVersion) => new(
        Id(item.CorrectionId),
        Id(item.OrderId),
        item.Status,
        item.Reason,
        item.Version,
        orderVersion,
        Id(item.RequestedBy),
        item.ResolvedBy is null ? null : Id(item.ResolvedBy.Value),
        item.ResolvedAtUtc
    );

    private static DateTime LifecycleTime(WorkOrder order, DateTime? floor = null)
    {
        var now = DateTime.UtcNow;
        foreach (var prior in new[] {
            order.StartedAtUtc,
            order.SubmittedAtUtc,
            floor
        })
        {
            if (prior is { } value && now < value)
            {
                now = value;
            }
        }
        return now.AddTicks(-(now.Ticks % 10));
    }

    private void RecordLifecycleTransition(
        WorkOrder order,
        string from,
        ClaimsPrincipal user,
        string? reason,
        DateTime occurredAt
    )
    {
        db.Add(new OrderTransition
        {
            OrderId = order.OrderId,
            FromStatus = from,
            ToStatus = order.Status,
            ActorId = Actor(user),
            Principal = "USER:" + Id(Actor(user)),
            Reason = reason,
            OccurredAtUtc = occurredAt
        });
    }
}


