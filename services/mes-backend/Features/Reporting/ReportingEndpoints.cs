using FactoryMES.Backend.Features.Orders;
using FactoryMES.Contracts;
using FactoryMES.Foundation;
using Microsoft.AspNetCore.Authorization;

namespace FactoryMES.Backend.Features.Reporting;

// HTTP 계약 경계. 공개 API는 사용자 정책을, 내부 API는 서비스 토큰 정책을 적용하고 업무 처리는 서비스에 위임한다.
public static class ReportingEndpoints
{
    // 같은 출처의 업무 화면 요청을 등록한다. 각 경로의 관리자·작업자 정책과 변경 요청 보호를 유지한다.
    public static void MapPublic(RouteGroupBuilder group)
    {
        group.MapGet("/reports/daily-production", async (
            string businessDate,
            int? limit,
            string? cursor,
            ReportingService service
        ) => await service.DailyAsync(
            businessDate,
            limit,
            cursor
        )).RequireAuthorization("Admin");

        group.MapGet("/reports/daily-production/status", async (
            string businessDate,
            ReportingService service
        ) => await service.StatusAsync(businessDate)).RequireAuthorization("Admin");

        group.MapPost("/reports/daily-production/rebuild", async (
            ReportingRunRequest input,
            HttpContext http,
            ReportingService service
        ) => Results.Accepted(
            value: await service.QueueAsync(input, http.User)
        )).RequireAuthorization("Admin");

        group.MapGet("/job-runs", async (
            int? limit,
            string? cursor,
            ReportingService service
        ) => await service.RunsAsync(limit, cursor)).RequireAuthorization("Admin");

        group.MapGet("/job-runs/{jobRunId}", async (
            string jobRunId,
            ReportingService service
        ) => await service.RunAsync(jobRunId)).RequireAuthorization("Admin");
    }

    // 내부 호출의 audience·scope를 정책에 연결해 사용자 로그인 권한과 서비스 권한을 분리한다.
    public static void AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy("ReportingRun", policy => policy
            .AddAuthenticationSchemes(ServiceFoundation.ServiceScheme)
            .RequireAuthenticatedUser()
            .RequireClaim("client_id", "jobs-worker")
            .RequireClaim("scope", "jobs:reporting:run"));
    }

    public static void MapReportingInternal(this WebApplication app)
    {
        var group = app.MapGroup("/internal/v1/reporting")
            .RequireAuthorization("ReportingRun");
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
        group.MapPost("/rebuild", async (
            ReportingRebuildRequest input,
            ReportingService service
        ) => await service.RebuildAsync(input));
        group.MapPost("/pending", async (
            DispatchBatchRequest input,
            ReportingService service
        ) => await service.PendingAsync(input));
    }
}

