using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using FactoryMES.Backend.Auth;
using FactoryMES.Backend.Features.Notifications;
using FactoryMES.Backend.Features.Orders;
using FactoryMES.Backend.Persistence;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Features.Analysis;

// 업무 백엔드 AI 분석 경계. Oracle 스냅샷·작업 상태를 보존하고 게이트웨이 호출·예약·재시도를 조정한다.
public sealed partial class AnalysisService(
    MesDbContext db,
    AnalysisCoordinator coordinator,
    IHttpClientFactory clients,
    ServiceTokenIssuer issuer,
    InboxService inbox,
    ILogger<AnalysisService>? logger = null
)
{
    private readonly ILogger<AnalysisService> log = logger
        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AnalysisService>.Instance;

    private static readonly JsonSerializerOptions Json = CollectionFormat.Json;
    private sealed record Intent(AnalysisRequest Request, AiResolvedConfig Configuration);

    private static void Check(
        [System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool valid,
        string message
    )
    {
        if (!valid)
        {
            throw new OrderFault(
                422,
                "AnalysisValidation",
                message
            );
        }
    }

    private static string JobId(string value)
    {
        Check(Guid.TryParseExact(
            value,
            "D",
            out _
        ), "분석 작업 ID를 확인하세요.");
        return Guid.Parse(value).ToString();
    }

    private static int Size(int? value)
    {
        if ((value ?? 50) is < 1 or > 100)
        {
            throw new OrderFault(
                400,
                "InvalidLimit",
                "목록은 1~100건입니다."
            );
        }

        return value ?? 50;
    }

    private static AiResolvedConfig Compile(IntegrationTarget target, AiProviderConfig settings)
    {
        var content = new AiConfigContent(
            target.TargetCode,
            "AI",
            "ai-http",
            target.ConfigVersion,
            target.Enabled && !target.Deleted,
            new AiSettings(
                settings.ProviderKind,
                settings.ApprovedEndpoint,
                settings.Model,
                settings.CredentialRef,
                settings.TimeoutMs
            )
        );

        return new AiResolvedConfig(content, CollectionFormat.Hash(content));
    }

    // 관리자 화면이 선택할 수 있는 설비·AI 대상 등 공개 가능한 등록 옵션을 조회한다.
    public async Task<AnalysisOptions> OptionsAsync()
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "OptionsAsync",
                "입력 처리 시작"
            );
            var rows = await (
                from target in db.Set<IntegrationTarget>()
                join settings in db.Set<AiProviderConfig>() on target.TargetId equals settings
                    .TargetId
                where target.Kind == "AI" && target.Deleted == EF.Parameter(false)
                orderby target.TargetId
                select new
                {
                    target,
                    settings
                }
            ).AsNoTracking().Take(100).ToArrayAsync();

            return new AnalysisOptions(
                CollectionFormat.UtcNow(),
                rows.Select(x => new AiTargetView(
                    x.target.TargetCode,
                    x.target.DisplayName,
                    x.target.Enabled,
                    x.target.Version,
                    Compile(x.target, x.settings)
                )).ToArray()
            );
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "OptionsAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "OptionsAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 관리자 설정을 검증해 허용된 AI 연동 대상을 저장한다. 키와 전체 설정은 로그에 남기지 않는다.
    public async Task<AiTargetView> CreateTargetAsync(
        MockAiTargetCreate input,
        ClaimsPrincipal user
    )
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "CreateTargetAsync",
                "입력 처리 시작"
            );
            Check(input.TargetId is not null
                && Regex.IsMatch(input.TargetId, "^[A-Za-z][A-Za-z0-9_.-]{0,63}$")
                && !string.IsNullOrWhiteSpace(input.DisplayName)
                && input.DisplayName.Trim().Length <= 100, "AI 대상 ID와 이름을 확인하세요.");
            Check(input.ProviderKind is "MOCK" or "LOCAL", "지원하는 AI 공급자는 MOCK 또는 LOCAL입니다.");
            var local = input.ProviderKind == "LOCAL";
            log.LogDebug("AI 대상 공급자 검증 완료: {Provider}", input.ProviderKind);
            await using var tx = await db.Database.BeginTransactionAsync();
            var target = new IntegrationTarget
            {
                TargetCode = input.TargetId!,
                Kind = "AI",
                DisplayName = input.DisplayName.Trim(),
                ConnectorType = "ai-http",
                Enabled = true,
                ConfigVersion = 1,
                ActivatedAtUtc = CollectionFormat.UtcNow()
            };
            db.Add(target);
            await db.SaveChangesAsync();
            var settings = new AiProviderConfig
            {
                TargetId = target.TargetId,
                Kind = "AI",
                ProviderKind = input.ProviderKind,
                ApprovedEndpoint = local ? LocalAiPolicy.Endpoint : null,
                Model = local ? LocalAiPolicy.Model : "mock-trend-v1",
                TimeoutMs = local ? LocalAiPolicy.TimeoutMs : 5000
            };
            db.Add(settings);
            db.Add(new AuditEvent
            {
                ActorId = OrderService.Actor(user),
                Principal = "USER:" + OrderService.Actor(user),
                Action = "analysis.target.created",
                EntityType = "INTEGRATION_TARGET",
                EntityId = target.TargetCode,
                Detail = CollectionFormat.Canonical(new
                {
                    provider = input.ProviderKind
                }),
                OccurredAtUtc = CollectionFormat.UtcNow()
            });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            log.LogInformation(
                "AI 대상 등록 완료: {ActorUserId}, {TargetId}, {Provider}",
                OrderService.Actor(user),
                target.TargetId,
                settings.ProviderKind
            );
            return new AiTargetView(
                target.TargetCode,
                target.DisplayName,
                target.Enabled,
                target.Version,
                Compile(target, settings)
            );
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "CreateTargetAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "CreateTargetAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    private static SnapshotDescriptor Snapshot(AnalysisSnapshot row) => new(
        OrderService.Id(row.SnapshotId),
        OrderService.Id(row.EquipmentId),
        row.PeriodStartUtc,
        row.PeriodEndUtc,
        row.InputVersion,
        CollectionFormat.Number(row.Coverage),
        row.InputHash
    );

    private async Task<AnalysisJobView> ViewAsync(IntegrationJob job)
    {
        log.LogDebug(
            "{Operation}: {Decision}",
            "ViewAsync",
            "입력 처리 시작"
        );
        var target = await db.Set<IntegrationTarget>()
            .AsNoTracking()
            .SingleAsync(x => x.TargetId == job.TargetId);
        var snapshot = job.SnapshotId is null ? null : await db.Set<AnalysisSnapshot>()
            .AsNoTracking()
            .SingleAsync(x => x.SnapshotId == job.SnapshotId);
        var results = await db.Set<AiAnalysisResult>()
            .AsNoTracking()
            .Where(x => x.JobId == job.JobId)
            .OrderBy(x => x.Attempt)
            .ToArrayAsync();

        return new AnalysisJobView(
            job.JobId,
            job.RequestId,
            target.TargetCode,
            job.Operation,
            job.State,
            job.Attempt,
            snapshot is null ? null : Snapshot(snapshot),
            job.ConfigVersion,
            job.DeadlineUtc,
            job.Version,
            job.Error,
            null,
            job.CorrelationId,
            results.Select(x => new AnalysisResultView(
                OrderService.Id(x.ResultId),
                x.JobId,
                x.Attempt,
                x.Provider,
                x.Model,
                x.InputPeriodStartUtc,
                x.InputPeriodEndUtc,
                CollectionFormat.Number(x.DataCoverage),
                x.ResultVersion,
                x.Risk ?? "UNKNOWN",
                JsonSerializer.Deserialize<AnalysisEvidence>(x.Evidence, Json)!,
                x.MockFlag,
                x.CompletedAtUtc,
                x.Accepted
            )).ToArray()
        );
    }

    public async Task<AnalysisJobView> GetAsync(string id)
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "GetAsync",
                "입력 처리 시작"
            );
            id = JobId(id);
            var job = await db.Set<IntegrationJob>()
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.JobId == id && x.Operation == "AI_ANALYZE")
                ?? throw new OrderFault(
                    404,
                    "AnalysisNotFound",
                    "분석 작업이 없습니다."
                );

            return await ViewAsync(job);
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "GetAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "GetAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    public async Task<PageResult<AnalysisJobView>> ListAsync(int? limit, string? cursor)
    {
        try
        {
            log.LogDebug(
                "{Operation}: {Decision}",
                "ListAsync",
                "입력 처리 시작"
            );
            var size = Size(limit);
            var query = db.Set<IntegrationJob>()
                .AsNoTracking()
                .Where(x => x.Operation == "AI_ANALYZE");
            if (cursor is not null)
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ListAsync",
                    "분기 일치 [cursor is not null]"
                );
                Check(cursor.Length <= 100, "목록 커서를 확인하세요.");
                var parts = cursor.Split(':');
                Check(parts.Length == 2 && long.TryParse(parts[0], out _), "목록 커서를 확인하세요.");
                var ticks = long.Parse(parts[0], CultureInfo.InvariantCulture);
                Check(ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks,
                    "목록 커서를 확인하세요.");
                var before = new DateTime(ticks, DateTimeKind.Utc);
                var key = JobId(parts[1]);
                query = query.Where(x => x.CreatedAtUtc < before
                    || (x.CreatedAtUtc == before && string.Compare(x.JobId, key) < 0));
            }
            else
            {
                log.LogDebug(
                    "{Operation}: {Decision}",
                    "ListAsync",
                    "분기 불일치 [cursor is not null]"
                );
            }

            var jobs = await query
                .OrderByDescending(x => x.CreatedAtUtc)
                .ThenByDescending(x => x.JobId)
                .Take(size + 1)
                .ToArrayAsync();
            var views = new List<AnalysisJobView>();
            foreach (var job in jobs.Take(size))
            {
                views.Add(await ViewAsync(job));
            }

            var last = jobs.Length > size ? jobs[size - 1] : null;
            return new PageResult<AnalysisJobView>(
                views.ToArray(),
                last is null ? null : last.CreatedAtUtc.Ticks + ":" + last.JobId
            );
        }
        catch (Exception processingError)
        {
            if (processingError is OperationCanceledException)
            {
                log.LogDebug("요청 처리가 취소됨: {Operation}", "ListAsync");
            }
            else
            {
                log.LogWarning(
                    "요청 처리 거절 또는 실패: {Operation}, {FailureCode}",
                    "ListAsync",
                    processingError is FactoryMES.Backend.Features.Orders.OrderFault fault
                        ? fault.Code : processingError.GetType().Name
                );
            }
            throw;
        }
    }

    // 등록 당시 보존한 요청·설정을 복원한다. 현재 설정으로 바꾸지 않아 재시도 의미를 유지한다.
    private async Task<Intent> IntentAsync(IntegrationJob job)
    {
        log.LogDebug(
            "{Operation}: {Decision}",
            "IntentAsync",
            "입력 처리 시작"
        );
        var audit = await db.Set<AuditEvent>()
            .AsNoTracking()
            .SingleAsync(x => x.EntityType == "INTEGRATION_JOB" && x.EntityId == job.JobId
                && x.Action == "analysis.requested");

        return JsonSerializer.Deserialize<Intent>(audit.Detail, Json)!;
    }
}






