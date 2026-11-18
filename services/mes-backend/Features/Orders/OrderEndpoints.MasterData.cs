using FactoryMES.Backend.Persistence;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Backend.Features.Orders;

public static partial class OrderEndpoints
{
    private static void MapMasterData(RouteGroupBuilder group)
    {
        group.MapGet(
            "/products/{id}",
            async (string id, MasterDataService service) => await service.GetProductAsync(id)
        ).RequireAuthorization("Admin");
        group.MapPost(
            "/products",
            async (
                ProductCreate input,
                HttpContext http,
                MasterDataService service
            ) =>
            {
                var result = await service.CreateProductAsync(input, http.User);
                return Results.Created("/api/v1/products/" + result.ProductId, result);
            }
        ).RequireAuthorization("Admin");
        group.MapPut(
            "/products/{id}",
            async (
                string id,
                ProductUpdate input,
                HttpContext http,
                MasterDataService service
            ) => await service.UpdateProductAsync(
                id,
                input,
                http.User
            )
        ).RequireAuthorization("Admin");
        group.MapGet(
            "/lines/{id}",
            async (string id, MasterDataService service) => await service.GetLineAsync(id)
        ).RequireAuthorization("Admin");
        group.MapPost(
            "/lines",
            async (
                LineCreate input,
                HttpContext http,
                MasterDataService service
            ) =>
            {
                var result = await service.CreateLineAsync(input, http.User);
                return Results.Created("/api/v1/lines/" + result.LineId, result);
            }
        ).RequireAuthorization("Admin");
        group.MapPut(
            "/lines/{id}",
            async (
                string id,
                LineUpdate input,
                HttpContext http,
                MasterDataService service
            ) => await service.UpdateLineAsync(
                id,
                input,
                http.User
            )
        ).RequireAuthorization("Admin");
        group.MapGet(
            "/equipment/{id}",
            async (string id, MasterDataService service) => await service.GetEquipmentAsync(id)
        ).RequireAuthorization("Admin");
        group.MapPost(
            "/equipment",
            async (
                EquipmentCreate input,
                HttpContext http,
                MasterDataService service
            ) =>
            {
                var result = await service.CreateEquipmentAsync(input, http.User);
                return Results.Created("/api/v1/equipment/" + result.EquipmentId, result);
            }
        ).RequireAuthorization("Admin");
        group.MapPut(
            "/equipment/{id}",
            async (
                string id,
                EquipmentUpdate input,
                HttpContext http,
                MasterDataService service
            ) => await service.UpdateEquipmentAsync(
                id,
                input,
                http.User
            )
        ).RequireAuthorization("Admin");
        group.MapGet(
            "/users/{id}",
            async (string id, MasterDataService service) => await service.GetUserAsync(id)
        ).RequireAuthorization("Admin");
        group.MapPost(
            "/users",
            async (
                UserCreate input,
                HttpContext http,
                MasterDataService service
            ) =>
            {
                var result = await service.CreateUserAsync(input, http.User);
                return Results.Created("/api/v1/users/" + result.UserId, result);
            }
        ).RequireAuthorization("Admin");
        group.MapPut(
            "/users/{id}",
            async (
                string id,
                UserUpdate input,
                HttpContext http,
                MasterDataService service
            ) => await service.UpdateUserAsync(
                id,
                input,
                http.User
            )
        ).RequireAuthorization("Admin");
        group.MapGet(
            "/lines",
            async (
                MesDbContext db,
                HttpContext http,
                int? limit,
                string? cursor,
                bool? active
            ) =>
            {
                var size = Size(limit);
                var after = cursor is null ? 0 : OrderService.ParseId(cursor);
                var query = db.Set<Line>().Where(x => x.LineId > after);
                if (!http.User.IsInRole("ADMIN"))
                {
                    var actor = OrderService.Actor(http.User);
                    query = query.Where(x => db.Set<Equipment>().Any(e => e.LineId == x.LineId
                        && db.Set<WorkOrder>().Any(o => o.EquipmentId == e.EquipmentId
                            && o.AssigneeId == actor && o.Status != "DRAFT")));
                }
                if (active is not null)
                {
                    query = query.Where(x => x.Active == active);
                }
                var rows = await query.OrderBy(x => x.LineId).Take(size + 1).ToListAsync();
                return new PageResult<LineChoice>(
                    rows.Take(size).Select(MasterDataService.View).ToArray(),
                    rows.Count > size ? OrderService.Id(rows[size - 1].LineId) : null
                );
            }
        );
    }
}

