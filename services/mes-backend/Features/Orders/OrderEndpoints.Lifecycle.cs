using FactoryMES.Contracts;

namespace FactoryMES.Backend.Features.Orders;

public static partial class OrderEndpoints
{
    private static void MapLifecycle(RouteGroupBuilder group)
    {
        group.MapPost("/orders/{orderId}/pause", async (
            string orderId,
            PauseRequest input,
            HttpContext http,
            OrderService service
        ) => await service.PauseAsync(
            orderId,
            input,
            http.User
        ))
            .RequireAuthorization("Worker");

        group.MapPost("/orders/{orderId}/resume", async (
            string orderId,
            Command input,
            HttpContext http,
            OrderService service
        ) => await service.ResumeAsync(
            orderId,
            input,
            http.User
        ))
            .RequireAuthorization("Worker");

        group.MapGet("/orders/{orderId}/corrections", async (
            string orderId,
            HttpContext http,
            OrderService service,
            int? limit,
            string? cursor
        ) => await service.CorrectionsAsync(
            orderId,
            http.User,
            limit,
            cursor
        ));

        group.MapPost("/orders/{orderId}/corrections", async (
            string orderId,
            CorrectionOpen input,
            HttpContext http,
            OrderService service
        ) =>
        {
            var correction = await service.OpenCorrectionAsync(
                orderId,
                input,
                http.User
            );
            return Results.Created(
                "/api/v1/orders/" + orderId + "/corrections",
                correction
            );
        }).RequireAuthorization("Admin");

        group.MapPost("/corrections/{correctionId}/resolve", async (
            string correctionId,
            CorrectionResolve input,
            HttpContext http,
            OrderService service
        ) => await service.ResolveCorrectionAsync(
            correctionId,
            input,
            http.User
        ))
            .RequireAuthorization("Admin");
    }
}

