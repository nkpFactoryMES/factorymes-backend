using FactoryMES.Backend.Features.Orders;
using FactoryMES.Contracts;
using FactoryMES.Foundation;
using Microsoft.AspNetCore.Authorization;

namespace FactoryMES.Backend.Features.Analysis;

// HTTP 계약 경계. 공개 API는 사용자 정책을, 내부 API는 서비스 토큰 정책을 적용하고 업무 처리는 서비스에 위임한다.
public static class AnalysisEndpoints
{
    // 같은 출처의 업무 화면 요청을 등록한다. 각 경로의 관리자·작업자 정책과 변경 요청 보호를 유지한다.
    public static void MapPublic(RouteGroupBuilder group)
    {
        group.MapGet("/analysis-options", async (AnalysisService service) =>
            await service.OptionsAsync()).RequireAuthorization("Admin");
        group.MapPost("/ai-targets", async (
            MockAiTargetCreate input,
            HttpContext http,
            AnalysisService service
        ) => Results.Created(
            "/api/v1/analysis-options",
            await service.CreateTargetAsync(input, http.User)
        )).RequireAuthorization("Admin");
        group.MapPost("/analyses", async (
            AnalysisRequest input,
            HttpContext http,
            AnalysisService service
        ) => Results.Accepted(value: await service.QueueAsync(input, http.User)))
            .RequireAuthorization("Admin");
        group.MapGet("/analyses", async (
            int? limit,
            string? cursor,
            AnalysisService service
        ) => await service.ListAsync(limit, cursor)).RequireAuthorization("Admin");
        group.MapGet("/analyses/{jobId}", async (string jobId, AnalysisService service) =>
            await service.GetAsync(jobId)).RequireAuthorization("Admin");
    }

    // 내부 호출의 audience·scope를 정책에 연결해 사용자 로그인 권한과 서비스 권한을 분리한다.
    public static void AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy("AnalysisDispatch", policy => policy
            .AddAuthenticationSchemes(ServiceFoundation.ServiceScheme)
            .RequireAuthenticatedUser()
            .RequireClaim("client_id", "jobs-worker")
            .RequireClaim("scope", "analysis:request"));
        options.AddPolicy("AnalysisInput", policy => policy
            .AddAuthenticationSchemes(ServiceFoundation.ServiceScheme)
            .RequireAuthenticatedUser()
            .RequireClaim("client_id", "ai-connector")
            .RequireClaim("scope", "analysis:input:read"));
    }

    public static void MapAnalysisInternal(this WebApplication app)
    {
        var group = app.MapGroup("/internal/v1");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try
            {
                return await next(context);
            }
            catch (OrderFault fault)
            {
                return Results.Problem(
                    statusCode: fault.Status,
                    title: fault.Message,
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = fault.Code
                    }
                );
            }
        });
        group.MapPost("/analyses/dispatch-batch", async (
            DispatchBatchRequest input,
            AnalysisService service
        ) => await service.DispatchAsync(input)).RequireAuthorization("AnalysisDispatch");
        group.MapPost("/analyses/scheduled", async (AnalysisService service) =>
            await service.ScheduledAsync()).RequireAuthorization("AnalysisDispatch");
        group.MapGet("/analysis-inputs/{jobId}", async (
            string jobId,
            long attempt,
            AnalysisService service
        ) => await service.InputAsync(jobId, attempt)).RequireAuthorization("AnalysisInput");
    }
}

