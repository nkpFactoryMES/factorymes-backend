using FactoryMES.Backend.Persistence;
using FactoryMES.Backend.Persistence.Entities;
using FactoryMES.Contracts;
using FactoryMES.Foundation;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Oracle.ManagedDataAccess.Client;
namespace FactoryMES.Backend.Features.Orders;
// Cookie 인증 브라우저의 작업지시 요청을 서비스에 전달한다. 상태 전이·담당자·실적 검증은 서비스가 수행한다.
public static partial class OrderEndpoints
{
    public static void MapOrders(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1").RequireAuthorization(
            new AuthorizeAttribute {
                AuthenticationSchemes = SessionCookies.Scheme,
                Roles = "ADMIN,WORKER"
            }
        );
        group.AddEndpointFilter(
            async (context, next) =>
            {
                var http = context.HttpContext;
                http.Response.Headers.CacheControl = "no-store";
                try
                {
                    if (!HttpMethods.IsGet(http.Request.Method))
                    {
                        var origin = http.Request.Headers.Origin.ToString();
                        var allowed = app.Configuration["Security:PublicOrigin"]
                            ?? "https://localhost:7443";
                        if (!http.Request.IsHttps || (origin.Length > 0 && origin != allowed))
                        {
                            throw new OrderFault(
                                403,
                                "CsrfFailed",
                                "요청 출처를 확인할 수 없습니다."
                            );
                        }
                        await http.RequestServices.GetRequiredService<IAntiforgery>()
                            .ValidateRequestAsync(http);
                    }
                    return await next(context);
                }
                catch (AntiforgeryValidationException)
                {
                    return Problem(
                        403,
                        "CsrfFailed",
                        "요청 확인에 실패했습니다. 다시 로그인해 주세요."
                    );
                }
                catch (OrderFault fault)
                {
                    return Problem(
                        fault.Status,
                        fault.Code,
                        fault.Message
                    );
                }
                catch (DbUpdateConcurrencyException)
                {
                    return Problem(
                        409,
                        "VersionConflict",
                        "다른 변경이 있습니다. 새로고침해 주세요."
                    );
                }
                catch (DbUpdateException error)when (error.InnerException is OracleException oracle
                    && oracle.Number is 1 or 60 or 2291)
                {
                    return Problem(
                        409,
                        "DataConflict",
                        "다른 작업과 충돌했습니다. 새로고침해 주세요."
                    );
                }
            }
        );
        group.MapGet(
            "/auto-counter-options",
            async (string equipmentId, OrderService service) =>
                await service.CounterOptionsAsync(equipmentId)
        ).RequireAuthorization("Admin");
        group.MapPost(
            "/orders/{orderId}/counter-rebase",
            async (
                string orderId,
                ReasonCommand input,
                HttpContext http,
                OrderService service
            ) =>
                await service.RebaseCounterAsync(
                    orderId,
                    input,
                    http.User
                )
        );
        group.MapGet(
            "/orders",
            async (
                HttpContext http,
                OrderService service,
                int? limit,
                string? cursor,
                string? status,
                string? equipmentId,
                string? assigneeId
            ) => await service.ListAsync(
                http.User,
                limit,
                cursor,
                status,
                equipmentId,
                assigneeId
            )
        );
        group.MapGet(
            "/orders/{orderId}",
            async (
                string orderId,
                HttpContext http,
                OrderService service
            ) => await service.GetAsync(orderId, http.User)
        );
        group.MapPost(
            "/orders",
            async (
                OrderCreate input,
                HttpContext http,
                OrderService service
            ) =>
            {
                var result = await service.CreateAsync(input, http.User);
                return Results.Created("/api/v1/orders/" + result.OrderId, result);
            }
        ).RequireAuthorization("Admin");
        group.MapPost(
            "/orders/{orderId}/release",
            async (
                string orderId,
                Command input,
                HttpContext http,
                OrderService service
            ) => await service.TransitionAsync(
                orderId,
                input.ExpectedVersion,
                "release",
                null,
                http.User
            )
        ).RequireAuthorization("Admin");
        group.MapPost(
            "/orders/{orderId}/close",
            async (
                string orderId,
                ReasonCommand input,
                HttpContext http,
                OrderService service
            ) => await service.TransitionAsync(
                orderId,
                input.ExpectedVersion,
                "close",
                input.Reason,
                http.User
            )
        ).RequireAuthorization("Admin");
        group.MapPost(
            "/orders/{orderId}/start",
            async (
                string orderId,
                Command input,
                HttpContext http,
                OrderService service
            ) => await service.TransitionAsync(
                orderId,
                input.ExpectedVersion,
                "start",
                null,
                http.User
            )
        ).RequireAuthorization("Worker");
        group.MapPost(
            "/orders/{orderId}/submit",
            async (
                string orderId,
                SubmitRequest input,
                HttpContext http,
                OrderService service
            ) => await service.TransitionAsync(
                orderId,
                input.ExpectedVersion,
                "submit",
                input.CompletionReason,
                http.User
            )
        ).RequireAuthorization("Worker");
        group.MapPut(
            "/orders/{orderId}/checklist",
            async (
                string orderId,
                ChecklistUpdate input,
                HttpContext http,
                OrderService service
            ) => await service.ChecklistAsync(
                orderId,
                input,
                http.User
            )
        ).RequireAuthorization("Worker");
        group.MapPost(
            "/orders/{orderId}/entries",
            async (
                string orderId,
                EntryBatch input,
                HttpContext http,
                OrderService service
            ) => await service.AppendAsync(
                orderId,
                input,
                http.User
            )
        ).RequireAuthorization("Worker");
        group.MapGet(
            "/orders/{orderId}/entries",
            async (
                string orderId,
                int? limit,
                string? cursor,
                HttpContext http,
                OrderService service,
                MesDbContext db
            ) =>
            {
                await service.GetAsync(orderId, http.User);
                var id = OrderService.ParseId(orderId);
                var size = limit ?? 50;
                if (size is < 1 or > 100)
                {
                    throw new OrderFault(
                        400,
                        "InvalidLimit",
                        "목록 크기는 1~100입니다."
                    );
                }
                var query = db.Set<ProductionEntry>().Where(x => x.OrderId == id);
                if (cursor is not null)
                {
                    var after = OrderService.ParseId(cursor);
                    query = query.Where(x => x.EntryId > after);
                }
                var rows = await query.OrderBy(x => x.EntryId).Take(size + 1).ToListAsync();
                return new PageResult<EntryView>(
                    rows.Take(size).Select(OrderService.Entry).ToArray(),
                    rows.Count > size ? OrderService.Id(rows[size - 1].EntryId) : null
                );
            }
        );
        MapChoices(group);
        MapLifecycle(group);
        MapMasterData(group);
        MapDraft(group);
        FactoryMES.Backend.Features.Collection.CollectionEndpoints.MapPublic(group);
        FactoryMES.Backend.Features.Alarms.AlarmEndpoints.MapPublic(group);
        FactoryMES.Backend.Features.Notifications.DeliveryEndpoints.MapPublic(group);
        FactoryMES.Backend.Features.Notifications.NotificationSettingsEndpoints.MapPublic(group);
        FactoryMES.Backend.Features.Reporting.ReportingEndpoints.MapPublic(group);
        FactoryMES.Backend.Features.Analysis.AnalysisEndpoints.MapPublic(group);
    }

    private static IResult Problem(
        int status,
        string code,
        string message
    ) => Results.Problem(
        statusCode: status,
        title: message,
        extensions: new Dictionary<string, object?> {
            ["code"] = code
        }
    );

    private static void MapChoices(RouteGroupBuilder group)
    {
        // Assignment choices are filtered on the server before keyset pagination.
        group.MapGet(
            "/products",
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
                var query = db.Set<Product>().Where(x => x.ProductId > after);
                if (!http.User.IsInRole("ADMIN"))
                {
                    var actor = OrderService.Actor(http.User);
                    query = query.Where(x => db.Set<WorkOrder>().Any(o => o.ProductId == x.ProductId
                        && o.AssigneeId == actor && o.Status != "DRAFT"));
                }
                if (active is not null)
                {
                    query = query.Where(x => x.Active == active);
                }
                var rows = await query.OrderBy(x => x.ProductId).Take(size + 1).ToListAsync();
                return new PageResult<ProductChoice>(
                    rows.Take(size).Select(
                        x => new ProductChoice(
                            OrderService.Id(x.ProductId),
                            x.Code,
                            x.Name,
                            x.Unit,
                            x.Active,
                            x.Version
                        )
                    ).ToArray(),
                    rows.Count > size ? OrderService.Id(rows[size - 1].ProductId) : null
                );
            }
        );
        group.MapGet(
            "/equipment",
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
                var query = db.Set<Equipment>().Where(x => x.EquipmentId > after);
                if (!http.User.IsInRole("ADMIN"))
                {
                    var actor = OrderService.Actor(http.User);
                    query = query.Where(x => db.Set<WorkOrder>().Any(o => o.EquipmentId == x
                        .EquipmentId
                        && o.AssigneeId == actor && o.Status != "DRAFT"));
                }
                if (active is not null)
                {
                    query = query.Where(x => x.Active == active);
                }
                var rows = await query.OrderBy(x => x.EquipmentId).Take(size + 1).ToListAsync();
                return new PageResult<EquipmentChoice>(
                    rows.Take(size).Select(
                        x => new EquipmentChoice(
                            OrderService.Id(x.EquipmentId),
                            x.Code,
                            x.Name,
                            OrderService.Id(x.LineId),
                            x.Active,
                            x.Version
                        )
                    ).ToArray(),
                    rows.Count > size ? OrderService.Id(rows[size - 1].EquipmentId) : null
                );
            }
        );
        group.MapGet(
            "/users",
            async (
                MesDbContext db,
                int? limit,
                string? cursor,
                string? role,
                bool? active,
                bool? assignable
            ) =>
            {
                var size = Size(limit);
                var after = cursor is null ? 0 : OrderService.ParseId(cursor);
                var query = db.Set<UserAccount>().Where(x => x.UserId > after);
                if (active is not null)
                {
                    query = query.Where(x => x.Active == active);
                }
                if (role is not null)
                {
                    if (role is not ("ADMIN" or "WORKER"))
                    {
                        throw new OrderFault(
                            400,
                            "InvalidRole",
                            "역할 필터를 확인해 주세요."
                        );
                    }
                    query = query
                        .Where(x => db.Set<UserRole>().Any(r => r.UserId == x.UserId
                        && r.Role == role));
                }
                if (assignable == true)
                {
                    query = query.Where(
                        x => x.Active == EF.Parameter(true)
                            && db.Set<UserRole>().Any(r => r.UserId == x.UserId
                            && r.Role == "WORKER")
                            && !db.Set<UserRole>().Any(r => r.UserId == x.UserId
                            && r.Role == "ADMIN")
                    );
                }
                var rows = await query.OrderBy(x => x.UserId).Take(size + 1).ToListAsync();
                var users = new List<UserChoice>();
                foreach (var x in rows.Take(size))
                {
                    var roles = await db.Set<UserRole>()
                        .Where(r => r.UserId == x.UserId)
                        .Select(r => r.Role)
                        .ToArrayAsync();
                    users.Add(
                        new UserChoice(
                            OrderService.Id(x.UserId),
                            x.Login,
                            x.Name,
                            roles,
                            x.Email,
                            x.Active,
                            x.Version
                        )
                    );
                }
                return new PageResult<UserChoice>(
                    users.ToArray(),
                    rows.Count > size ? OrderService.Id(rows[size - 1].UserId) : null
                );
            }
        ).RequireAuthorization("Admin");
    }

    private static int Size(int? limit)
    {
        var size = limit ?? 50;
        if (size is < 1 or > 100)
        {
            throw new OrderFault(
                400,
                "InvalidLimit",
                "목록 크기는 1~100입니다."
            );
        }
        return size;
    }
}

