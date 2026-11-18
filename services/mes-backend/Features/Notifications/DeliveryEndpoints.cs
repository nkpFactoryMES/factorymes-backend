using FactoryMES.Backend.Features.Orders;
using FactoryMES.Contracts;
using FactoryMES.Foundation;
using Microsoft.AspNetCore.Authorization;

namespace FactoryMES.Backend.Features.Notifications;

// HTTP 계약 경계. 공개 API는 사용자 정책을, 내부 API는 서비스 토큰 정책을 적용하고 업무 처리는 서비스에 위임한다.
public static class DeliveryEndpoints
{
    // 같은 출처의 업무 화면 요청을 등록한다. 각 경로의 관리자·작업자 정책과 변경 요청 보호를 유지한다.
    public static void MapPublic(RouteGroupBuilder group)
    {
        group.MapPost("/local-notifications", async (
            LocalNotificationRequest input,
            HttpContext http,
            DeliveryService service
        ) =>
        {
            var (result, created) = await service.CreateLocalAsync(input, http.User);
            return created ? Results.Created("/api/v1/deliveries?notificationId="
                + result.NotificationId, result) : Results.Ok(result);
        }).RequireAuthorization("Admin");
        group.MapGet("/delivery-local-statuses", async (
            string? deliveryIds,
            DeliveryService service
        ) => await service.LocalStatusesAsync(deliveryIds)).RequireAuthorization("Admin");
        group.MapGet("/deliveries/{deliveryId}/local-receipt", async (
            string deliveryId,
            DeliveryService service
        ) => await service.LocalReceiptAsync(deliveryId)).RequireAuthorization("Admin");
        group.MapGet("/deliveries", async (
            int? limit,
            string? cursor,
            string? status,
            string? channel,
            string? notificationId,
            DeliveryService service
        ) =>
            await service.ListAsync(
                limit,
                cursor,
                status,
                channel,
                notificationId
            )).RequireAuthorization("Admin");
        group.MapPost("/deliveries/{deliveryId}/retry", async (
            string deliveryId,
            ReasonCommand input,
            HttpContext http,
            DeliveryService service
        ) =>
            await service.RetryAsync(
                deliveryId,
                input,
                http.User
            )).RequireAuthorization("Admin");
        group.MapGet("/delivery-mock-statuses", async (string? deliveryIds,
            DeliveryService service) =>
            await service.MockStatusesAsync(deliveryIds)).RequireAuthorization("Admin");
        group.MapGet("/deliveries/{deliveryId}/mock-receipt", async (string deliveryId,
            DeliveryService service) =>
            await service.ReceiptAsync(deliveryId)).RequireAuthorization("Admin");
        group.MapPost("/mock-notifications", async (
            MockNotificationRequest input,
            HttpContext http,
            DeliveryService service
        ) =>
        {
            var (result, created) = await service.CreateMockAsync(input, http.User);
            return created ? Results.Created("/api/v1/deliveries?notificationId=" + result
                .NotificationId, result)
                : Results.Ok(result);
        }).RequireAuthorization("Admin");
        group.MapGet("/outbox-events/{outboxId}/retry-state", async (
            string outboxId,
            DeliveryService service
        ) => await service.OutboxRetryStateAsync(outboxId)).RequireAuthorization("Admin");
        group.MapPost("/outbox-events/{outboxId}/retry", async (
            string outboxId,
            OutboxRetryRequest input,
            HttpContext http,
            DeliveryService service
        ) => Results.Accepted(value: await service.RetryOutboxAsync(
            outboxId,
            input,
            http.User
        ))).RequireAuthorization("Admin");
        group.MapGet("/outbox-events", async (
            int? limit,
            string? cursor,
            string? status,
            DeliveryService service
        ) =>
            await service.OutboxAsync(
                limit,
                cursor,
                status
            )).RequireAuthorization("Admin");
    }

    // 내부 호출의 audience·scope를 정책에 연결해 사용자 로그인 권한과 서비스 권한을 분리한다.
    public static void AddPolicies(AuthorizationOptions options)
    {
        Add(
            options,
            "OutboxDispatch",
            "jobs:outbox:dispatch"
        );
        Add(
            options,
            "DeliveryDispatch",
            "jobs:deliveries:dispatch"
        );
    }

    private static void Add(
        AuthorizationOptions options,
        string name,
        string scope
    ) =>
        options.AddPolicy(name, policy => policy.AddAuthenticationSchemes(ServiceFoundation
            .ServiceScheme)
            .RequireAuthenticatedUser().RequireClaim("client_id", "jobs-worker")
                .RequireClaim("scope", scope));

    public static void MapDeliveryInternal(this WebApplication app)
    {
        var group = app.MapGroup("/internal/v1/jobs");
        group.AddEndpointFilter(async (context, next) =>
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
        group.MapPost("/outbox/dispatch-batch", async (
            DispatchBatchRequest input,
            DeliveryService service,
            CancellationToken ct
        ) =>
            await service.DispatchOutboxAsync(input, ct)).RequireAuthorization("OutboxDispatch");
        group.MapPost("/deliveries/dispatch-batch", async (
            DispatchBatchRequest input,
            DeliveryService service,
            CancellationToken ct
        ) =>
            await service.DispatchDeliveriesAsync(input, ct))
                .RequireAuthorization("DeliveryDispatch");
    }
}




