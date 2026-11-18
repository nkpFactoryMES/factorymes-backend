using FactoryMES.Backend.Features.Orders;
using FactoryMES.Contracts;
using FactoryMES.Foundation;
using Microsoft.AspNetCore.Authorization;

namespace FactoryMES.Backend.Features.Collection;

// 관리자 연결 설정과 내부 게이트웨이 수집 경로를 등록한다. 사용자 권한과 내부 서비스 scope를 경로별로 분리한다.
public static class CollectionEndpoints
{
    public static void MapPublic(RouteGroupBuilder group)
    {
        group.MapGet("/integration-targets", async (
            int? limit,
            string? cursor,
            CollectionService service
        )
            => await service.ListAsync(limit, cursor)).RequireAuthorization("Admin");
        group.MapGet("/integration-targets/{targetId}", async (string targetId,
            CollectionService service)
            => await service.GetAsync(targetId)).RequireAuthorization("Admin");
        group.MapPost("/integration-targets", async (
            EquipmentTargetCreate input,
            HttpContext http,
            CollectionService service
        ) =>
        {
            var result = await service.CreateAsync(input, http.User);
            return Results.Created("/api/v1/integration-targets/" + result.TargetId, result);
        }).RequireAuthorization("Admin");
        group.MapPut("/integration-targets/{targetId}/configuration", async (
            string targetId,
            TargetConfigUpdate input,
            HttpContext http,
            CollectionService service
        )
            => await service.UpdateAsync(
                targetId,
                input,
                http.User
            )).RequireAuthorization("Admin");
        group.MapPut("/integration-targets/{targetId}/state", async (
            string targetId,
            TargetStateUpdate input,
            HttpContext http,
            CollectionService service
        ) =>
        {
            var current = await service.GetAsync(targetId);
            return await service.UpdateAsync(
                targetId,
                new TargetConfigUpdate(
                    input.ExpectedVersion,
                    input.ExpectedConfigVersion,
                    current.DisplayName,
                    input.Enabled,
                    input.Reason,
                    current.Config.Content.Settings
                ),
                http.User
            );
        }).RequireAuthorization("Admin");
        group.MapGet("/integration-targets/{targetId}/samples", async (
            string targetId,
            int? limit,
            string? cursor,
            CollectionService service
        )
            => await service.SamplesAsync(
                targetId,
                limit,
                cursor
            )).RequireAuthorization("Admin");
    }

    public static void MapCollectionInternal(this WebApplication app)
    {
        var group = app.MapGroup("/internal/v1");
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
        group.MapGet("/integration/targets", async (
            int? limit,
            string? cursor,
            CollectionService service
        )
            => await service.ListAsync(limit, cursor)).RequireAuthorization("GatewayConfig");
        group.MapGet("/integration/targets/{targetId}/configuration",
            async (string targetId, CollectionService service) => await service
                .ConfigurationAsync(targetId))
            .RequireAuthorization("GatewayConfig");
        group.MapPost("/integration/leases/renew", async (LeaseRequest input,
            CollectionService service)
            => await service.LeaseAsync(input)).RequireAuthorization("GatewayLease");
        group.MapPost("/telemetry/batches", async (
            TelemetryBatch input,
            HttpContext http,
            CollectionService service
        )
            => await service.StoreAsync(input, http.TraceIdentifier))
                .RequireAuthorization("ConnectorTelemetry");
        group.MapPost("/connector-status", async (
            RuntimeReport input,
            HttpContext http,
            CollectionService service
        ) =>
        {
            await service.ReportAsync(input);
            return Results.Ok(new {
                accepted = true,
                correlationId = http.TraceIdentifier
            });
        }).RequireAuthorization("ConnectorRuntime");
    }

    public static void AddPolicies(AuthorizationOptions options)
    {
        Add(
            options,
            "GatewayConfig",
            "integration-gateway",
            "integration:config:read"
        );
        Add(
            options,
            "GatewayLease",
            "integration-gateway",
            "telemetry:lease:issue"
        );
        Add(
            options,
            "ConnectorTelemetry",
            "equipment-connector",
            "telemetry:write"
        );
        Add(
            options,
            "ConnectorRuntime",
            "equipment-connector",
            "integration:runtime:write"
        );
    }

    private static void Add(
        AuthorizationOptions options,
        string name,
        string client,
        string scope
    )
        => options.AddPolicy(name, policy => policy.AddAuthenticationSchemes(ServiceFoundation
            .ServiceScheme)
            .RequireAuthenticatedUser().RequireClaim("client_id", client).RequireClaim("scope",
                scope));
}



