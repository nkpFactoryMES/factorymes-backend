using FactoryMES.Backend.Features.Notifications;
using FactoryMES.Backend.Features.Orders;
using FactoryMES.Contracts;
using FactoryMES.Foundation;
using Microsoft.AspNetCore.Authorization;

namespace FactoryMES.Backend.Features.Alarms;

public static class AlarmEndpoints
{
    // 브라우저의 Cookie 인증 사용자를 서비스에 전달한다. 세부 설비 접근 범위는 서비스가 제한한다.
    public static void MapPublic(RouteGroupBuilder group)
    {
        group.MapGet("/alarm-rules", async (
            HttpContext http,
            AlarmService service,
            int? limit,
            string? cursor,
            string? equipmentId
        ) =>
            await service.RulesAsync(
                http.User,
                limit,
                cursor,
                equipmentId
            ));
        group.MapGet("/alarm-rules/{ruleId}", async (string ruleId, AlarmService service) =>
            await service.GetRuleAsync(ruleId)).RequireAuthorization("Admin");
        group.MapPost("/alarm-rules", async (
            RuleWrite input,
            HttpContext http,
            AlarmService service
        ) =>
        {
            var result = await service.CreateRuleAsync(input, http.User);
            return Results.Created("/api/v1/alarm-rules/" + result.RuleId, result);
        }).RequireAuthorization("Admin");
        group.MapPut("/alarm-rules/{ruleId}", async (
            string ruleId,
            RuleUpdate input,
            HttpContext http,
            AlarmService service
        ) =>
            await service.UpdateRuleAsync(
                ruleId,
                input,
                http.User
            )).RequireAuthorization("Admin");
        group.MapGet("/alarms", async (
            HttpContext http,
            AlarmService service,
            int? limit,
            string? cursor,
            string? equipmentId,
            string? status
        ) =>
            await service.ListAsync(
                http.User,
                limit,
                cursor,
                equipmentId,
                status
            ));
        group.MapGet("/alarms/{alarmId}", async (
            string alarmId,
            HttpContext http,
            AlarmService service
        ) =>
            await service.GetAsync(alarmId, http.User));
        group.MapPost("/alarms/{alarmId}/ack", async (
            string alarmId,
            Command input,
            HttpContext http,
            AlarmService service
        ) =>
            await service.AcknowledgeAsync(
                alarmId,
                input,
                http.User
            ));
        group.MapGet("/alarms/{alarmId}/actions", async (
            string alarmId,
            HttpContext http,
            AlarmService service,
            int? limit,
            string? cursor
        ) => await service.ActionsAsync(
            alarmId,
            http.User,
            limit,
            cursor
        ));
        group.MapPost("/alarms/{alarmId}/actions", async (
            string alarmId,
            ActionCreate input,
            HttpContext http,
            AlarmService service
        ) =>
        {
            var action = await service.CreateActionAsync(
                alarmId,
                input,
                http.User
            );
            return Results.Created("/api/v1/alarms/" + alarmId + "/actions", action);
        });
        group.MapPost("/alarm-actions/{actionId}/start", async (
            string actionId,
            ActionStart input,
            HttpContext http,
            AlarmService service
        ) => await service.StartActionAsync(
            actionId,
            input,
            http.User
        ));
        group.MapPost("/alarm-actions/{actionId}/complete", async (
            string actionId,
            ActionComplete input,
            HttpContext http,
            AlarmService service
        ) => await service.CompleteActionAsync(
            actionId,
            input,
            http.User
        ));
        group.MapPost("/alarms/{alarmId}/close", async (
            string alarmId,
            ReasonCommand input,
            HttpContext http,
            AlarmService service
        ) => await service.CloseAsync(
            alarmId,
            input,
            http.User,
            false
        )).RequireAuthorization("Admin");
        group.MapPost("/alarms/{alarmId}/force-close", async (
            string alarmId,
            ReasonCommand input,
            HttpContext http,
            AlarmService service
        ) => await service.CloseAsync(
            alarmId,
            input,
            http.User,
            true
        )).RequireAuthorization("Admin");
        group.MapGet("/inbox", async (
            HttpContext http,
            InboxService service,
            bool? unreadOnly,
            int? limit,
            string? cursor
        ) =>
            await service.ListAsync(
                http.User,
                unreadOnly,
                limit,
                cursor
            ));
        group.MapPut("/inbox/{notificationId}/read", async (
            string notificationId,
            HttpContext http,
            InboxService service
        ) =>
        {
            await service.ReadAsync(notificationId, http.User);
            return new AckResponse(true, http.TraceIdentifier);
        });
    }

    // jobs-worker 서비스 토큰과 전용 scope를 요구하는 내부 감시 API다. 외부 사용자 경로와 분리한다.
    public static void MapAlarmInternal(this WebApplication app)
    {
        app.MapPost("/internal/v1/jobs/connections/watch", async (
            DispatchBatchRequest input,
            AlarmService service,
            ConnectionWatchCoordinator coordinator,
            CancellationToken ct
        ) =>
            await service.WatchAsync(
                input,
                coordinator,
                ct
            ))
            .RequireAuthorization("ConnectionsWatch")
            .AddEndpointFilter(async (context, next) =>
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                try { return await next(context); }
                catch (OrderFault fault)
                {
                    return Results.Problem(
                        statusCode: fault.Status,
                        title: fault.Message,
                        extensions: new Dictionary<string, object?> {
                            ["code"] = fault.Code
                        }
                    );
                }
            });
    }

    public static void AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy("ConnectionsWatch", policy => policy
            .AddAuthenticationSchemes(ServiceFoundation.ServiceScheme)
            .RequireAuthenticatedUser()
            .RequireClaim("client_id", "jobs-worker")
            .RequireClaim("scope", "jobs:connections:watch"));
    }
}




