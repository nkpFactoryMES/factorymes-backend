using FactoryMES.Contracts;

namespace FactoryMES.Backend.Features.Orders;

public static partial class OrderEndpoints
{
    private static void MapDraft(RouteGroupBuilder group)
    {
        group.MapPut(
            "/orders/{orderId}/draft",
            async (
                string orderId,
                OrderDraftUpdate input,
                HttpContext http,
                OrderService service
            ) => await service.UpdateDraftAsync(
                orderId,
                input,
                http.User
            )
        ).RequireAuthorization("Admin");
        group.MapPost(
            "/orders/{orderId}/cancel",
            async (
                string orderId,
                ReasonCommand input,
                HttpContext http,
                OrderService service
            ) => await service.CancelAsync(
                orderId,
                input,
                http.User
            )
        ).RequireAuthorization("Admin");
    }
}

