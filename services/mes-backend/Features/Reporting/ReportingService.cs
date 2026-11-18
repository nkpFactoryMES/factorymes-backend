using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using FactoryMES.Backend.Features.Collection;
using FactoryMES.Backend.Features.Orders;
using FactoryMES.Backend.Persistence;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Features.Reporting;

// 마감 생산실적 원장을 영업일·설비별 통계로 변환한다. 원본 작업·실적은 수정하지 않는다.
public sealed partial class ReportingService(
    MesDbContext db,
    ReportingCoordinator coordinator,
    ILogger<ReportingService>? logger = null
)
{
    private readonly ILogger<ReportingService> log = logger
        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ReportingService>.Instance;

    private const string Prefix = "daily-production:";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static DateOnly BusinessDay(string? value)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(9));
        if (!DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var day
        ) || day < new DateOnly(
            2000,
            1,
            1
        ) || day > today)
        {
            throw new OrderFault(
                422,
                "InvalidBusinessDate",
                "2000년 이후 오늘까지의 날짜를 입력하세요."
            );
        }

        return day;
    }

    private static DateTime DateKey(DateOnly day) =>
        day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

    private static string DayText(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo
        .InvariantCulture);

    private static int Size(int? limit)
    {
        if ((limit ?? 50) is < 1 or > 100)
        {
            throw new OrderFault(
                400,
                "InvalidLimit",
                "목록 크기는 1~100입니다."
            );
        }

        return limit ?? 50;
    }

    public static JobRunView View(JobRun row) => new(
        OrderService.Id(row.JobRunId),
        row.JobKey,
        row.ScheduledForUtc,
        row.Attempt,
        row.Status,
        row.StartedAtUtc,
        row.EndedAtUtc,
        row.AffectedRows,
        row.Error
    );

    // 영업일·설비 조건과 커서로 저장된 생산 통계를 조회한다. 조회 요청에서 재집계하지 않는다.
    public async Task<PageResult<DailyProductionView>> DailyAsync(
        string businessDate,
        int? limit,
        string? cursor
    )
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "DailyAsync",
                "입력 처리 시작"
            );
            var day = BusinessDay(businessDate);
            var size = Size(limit);
            var key = DateKey(day);
            var query = db.Set<DailyProduction>()
                .AsNoTracking()
                .Where(x => x.BusinessDate == key);

            if (cursor is not null)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "DailyAsync",
                    "분기 일치 [cursor is not null]"
                );
                var parts = cursor.Split(':');
                if (parts.Length != 2)
                {
                    log.LogDebug(
                        "{Operation}: {Decision}",
                        "DailyAsync",
                        "분기 일치 [parts.Length != 2]"
                    );
                    throw new OrderFault(
                        400,
                        "InvalidCursor",
                        "목록 커서를 확인하세요."
                    );
                }
                else
                {
                    log.LogDebug(
                        "{Operation}: {Decision}",
                        "DailyAsync",
                        "분기 불일치 [parts.Length != 2]"
                    );
                }

                var equipment = OrderService.ParseId(parts[0]);
                var product = OrderService.ParseId(parts[1]);
                query = query.Where(x => x.EquipmentId > equipment
                    || (x.EquipmentId == equipment && x.ProductId > product));
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "DailyAsync",
                    "분기 불일치 [cursor is not null]"
                );
            }

            var rows = await query
                .OrderBy(x => x.EquipmentId)
                .ThenBy(x => x.ProductId)
                .Take(size + 1)
                .ToArrayAsync();
            var items = rows.Take(size).Select(x => new DailyProductionView(
                DayText(day),
                OrderService.Id(x.EquipmentId),
                OrderService.Id(x.ProductId),
                x.TotalQty,
                x.RejectQty,
                x.RefreshedAtUtc
            )).ToArray();
            var next = rows.Length > size
                ? items[^1].EquipmentId + ":" + items[^1].ProductId
                : null;

            return new PageResult<DailyProductionView>(items, next);
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "DailyAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "DailyAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 해당 영업일의 집계 작업 상태와 최신 결과를 조회해 화면의 실행 상태를 구성한다.
    public async Task<ReportingStatusView> StatusAsync(string businessDate)
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "StatusAsync",
                "입력 처리 시작"
            );
            var day = BusinessDay(businessDate);
            var key = Prefix + DayText(day);
            var run = await db.Set<JobRun>()
                .AsNoTracking()
                .Where(x => x.JobKey == key)
                .OrderByDescending(x => x.JobRunId)
                .FirstOrDefaultAsync();

            return new ReportingStatusView(DayText(day), run is null ? null : View(run));
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "StatusAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "StatusAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 작업 식별자 커서로 집계 실행 이력을 제한된 개수로 조회한다.
    public async Task<PageResult<JobRunView>> RunsAsync(int? limit, string? cursor)
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "RunsAsync",
                "입력 처리 시작"
            );
            var size = Size(limit);
            var before = cursor is null ? long.MaxValue : OrderService.ParseId(cursor);
            var rows = await db.Set<JobRun>()
                .AsNoTracking()
                .Where(x => x.JobRunId < before)
                .OrderByDescending(x => x.JobRunId)
                .Take(size + 1)
                .ToArrayAsync();

            return new PageResult<JobRunView>(
                rows.Take(size).Select(View).ToArray(),
                rows.Length > size ? OrderService.Id(rows[size - 1].JobRunId) : null
            );
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "RunsAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "RunsAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 화면에서 선택한 집계 실행 식별자에 해당하는 이력을 조회한다.
    public async Task<JobRunView> RunAsync(string id)
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "RunAsync",
                "입력 처리 시작"
            );
            var key = OrderService.ParseId(id);
            var row = await db.Set<JobRun>()
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.JobRunId == key)
                ?? throw new OrderFault(
                    404,
                    "JobRunNotFound",
                    "집계 실행 이력이 없습니다."
                );

            return View(row);
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "RunAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "RunAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 관리자 요청의 영업일과 실행 중복을 검증하고 집계 대기 이력을 저장한다.
    public async Task<JobRunView> QueueAsync(
        ReportingRunRequest input,
        ClaimsPrincipal user
    )
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "QueueAsync",
                "입력 처리 시작"
            );
            var day = BusinessDay(input.BusinessDate);
            if (!Guid.TryParseExact(
                input.ClientRequestId,
                "D",
                out var requestId
            )
                || string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Trim().Length > 1000)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "QueueAsync",
                    "분기 일치 [!Guid.TryParseExact( input.ClientRequestId, \"D\", out var request...]"
                );
                throw new OrderFault(
                    422,
                    "ReportingValidation",
                    "요청 ID와 사유 1~1000자를 확인하세요."
                );
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "QueueAsync",
                    "분기 불일치 [!Guid.TryParseExact( input.ClientRequestId, \"D\", out var request...]"
                );
            }

            var actor = OrderService.Actor(user);
            var intent = new ReportingRunRequest(
                DayText(day),
                requestId.ToString(),
                input.Reason.Trim()
            );
            var action = "reporting.request:" + requestId;
            await coordinator.Gate.WaitAsync();
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await LockRunsAsync();
                var existing = await db.Set<AuditEvent>()
                    .SingleOrDefaultAsync(x => x.Action == action);
                if (existing is not null)
                {
                    log.LogDebug(
                        "{Operation}: {Decision}",
                        "QueueAsync",
                        "분기 일치 [existing is not null]"
                    );
                    if (existing.ActorId != actor || existing.Detail != JsonSerializer
                        .Serialize(intent, Json))
                    {
                        log.LogDebug(
                            "{Operation}: {Decision}",
                            "QueueAsync",
                            "분기 일치 [existing.ActorId != actor || existing.Detail != JsonSerializer ....]"
                        );
                        throw new OrderFault(
                            409,
                            "ReportingConflict",
                            "같은 요청 ID의 내용이 다릅니다."
                        );
                    }
                    else
                    {
                        log.LogDebug(
                            "{Operation}: {Decision}",
                            "QueueAsync",
                            "분기 불일치 [existing.ActorId != actor || existing.Detail != JsonSerializer ....]"
                        );
                    }

                    return await RunAsync(existing.EntityId!);
                }
                else
                {
                    log.LogDebug(
                        "{Operation}: {Decision}",
                        "QueueAsync",
                        "분기 불일치 [existing is not null]"
                    );
                }

                var run = await NewRunAsync(day);
                db.Add(new AuditEvent
                {
                    ActorId = actor,
                    Principal = "USER:" + OrderService.Id(actor),
                    Action = action,
                    EntityType = "JOB_RUN",
                    EntityId = OrderService.Id(run.JobRunId),
                    Detail = JsonSerializer.Serialize(intent, Json),
                    OccurredAtUtc = CollectionFormat.UtcNow()
                });
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                log.LogInformation(
                    "통계 재집계 요청 접수: {ActorUserId}, {JobRunId}, {BusinessDate}",
                    actor,
                    run.JobRunId,
                    day
                );
                return View(run);
            }
            finally
            {
                coordinator.Gate.Release();
            }
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "QueueAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "QueueAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    private Task<int> LockRunsAsync() =>
        db.Database.ExecuteSqlRawAsync("LOCK TABLE JOB_RUN IN EXCLUSIVE MODE");

    // 검증된 영업일로 새 대기 집계 이력을 준비한다. 호출자의 트랜잭션과 함께 저장한다.
    private async Task<JobRun> NewRunAsync(DateOnly day)
    {
        log.LogDebug(
            "{Operation}: {Decision}",
            "NewRunAsync",
            "입력 처리 시작"
        );
        var key = Prefix + DayText(day);
        var scheduled = DateTime.SpecifyKind(DateKey(day).AddHours(-9), DateTimeKind.Utc);
        var attempt = await db.Set<JobRun>()
            .Where(x => x.JobKey == key && x.ScheduledForUtc == scheduled)
            .Select(x => (long?)x.Attempt)
            .MaxAsync() ?? 0;
        var run = new JobRun
        {
            JobKey = key,
            ScheduledForUtc = scheduled,
            Attempt = checked(attempt + 1),
            Status = "RUNNING",
            StartedAtUtc = CollectionFormat.UtcNow()
        };
        db.Add(run);
        await db.SaveChangesAsync();

        return run;
    }
}




