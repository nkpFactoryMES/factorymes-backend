using FactoryMES.Backend.Features.Analysis;
using FactoryMES.Backend.Features.Reporting;
using FactoryMES.Backend.Features.Alarms;
using FactoryMES.Backend.Features.Notifications;
using FactoryMES.Backend.Features.Collection;
using FactoryMES.Backend.Features.Orders;
using FactoryMES.Foundation;
using FactoryMES.Backend.Auth;
using FactoryMES.Backend.Persistence;
using FactoryMES.Backend.Persistence.Entities;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Oracle.EntityFrameworkCore.Infrastructure;
using System.Threading.RateLimiting;
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

if (await HealthProbe.TryRunAsync(args))
    return;
var builder = WebApplication.CreateBuilder(args);
builder.AddLocalFoundation("mes-backend");
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    options.KnownProxies.Add(
        IPAddress.Parse(builder.Configuration["Security:TrustedProxyAddress"]
        ?? "127.0.0.1")
    );
});
var connectionString =
    SecretFiles.Read(
    builder.Configuration["Database:ConnectionStringFile"]
    ?? "/run/secrets/oracle_connection"
);
builder.Services.AddDbContext<MesDbContext>(
    options => options.UseOracle(
        connectionString,
        oracle => oracle.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion23)
    )
);
builder.Services.AddScoped<UserAuthentication>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<AutoProductionService>();
builder.Services.AddScoped<MasterDataService>();
builder.Services.AddScoped<CollectionService>();
builder.Services.AddScoped<AlarmService>();
builder.Services.AddScoped<InboxService>();
builder.Services.AddScoped<NotificationSettingsService>();
builder.Services.AddScoped<AnalysisService>();
builder.Services.AddSingleton<AnalysisCoordinator>();
builder.Services.AddAuthorization(AnalysisEndpoints.AddPolicies);
builder.Services.AddHttpClient("analysis-gateway", client => client.Timeout = TimeSpan.FromSeconds(5))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler {
        AllowAutoRedirect = false
    });
builder.Services.AddScoped<ReportingService>();
builder.Services.AddSingleton<ReportingCoordinator>();
builder.Services.AddAuthorization(ReportingEndpoints.AddPolicies);
builder.Services.AddScoped<DeliveryService>();
builder.Services.AddSingleton<MockDeliveryProvider>();
// 실제 로컬 SMTP/HTTP만 허용한다. 외부 주소·리디렉션·시스템 프록시를 사용하지 않는다.
builder.Services.AddSingleton<LocalDeliveryProvider>();
builder.Services.AddHttpClient("LocalDelivery", client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    UseProxy = false,
    ConnectTimeout = TimeSpan.FromSeconds(2)
});
builder.Services.AddSingleton<DeliveryCoordinator>();
builder.Services.AddAuthorization(DeliveryEndpoints.AddPolicies);
builder.Services.AddSingleton<ConnectionWatchCoordinator>();
builder.Services.AddAuthorization(AlarmEndpoints.AddPolicies);
builder.Services.AddAuthorization(CollectionEndpoints.AddPolicies);
builder.Services.AddHttpClient("configuration-publisher", client => client.Timeout = TimeSpan.FromSeconds(5))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler {
        AllowAutoRedirect = false
    });
builder.Services.AddHostedService<ConfigurationPublisher>();
builder.Services.AddScoped<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();
builder.Services.AddSingleton<ServiceTokenIssuer>();
builder.Services.AddMesCookies(
    builder.Configuration,
    options => options.Events.OnValidatePrincipal =
    async context =>
{
    var session =
        await context.HttpContext.RequestServices.GetRequiredService<UserAuthentication>()
            .ValidateAsync(
        context.Principal!,
        context.HttpContext.RequestAborted
    );
    if (session is null)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(SessionCookies.Scheme);
    }
}
);
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        "Admin",
        policy => policy.AddAuthenticationSchemes(SessionCookies.Scheme)
            .RequireAuthenticatedUser()
            .RequireRole("ADMIN")
    );
    options.AddPolicy(
        "Worker",
        policy => policy.AddAuthenticationSchemes(SessionCookies.Scheme)
            .RequireAuthenticatedUser()
            .RequireRole("WORKER")
            .RequireAssertion(context => !context.User.IsInRole("ADMIN"))
    );
});
builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("auth", o =>
{
    o.PermitLimit = 20;
    o.Window = TimeSpan.FromMinutes(1);
    o.QueueLimit = 0;
})
    .RejectionStatusCode = 429);
var app = builder.Build();
app.UseForwardedHeaders();
app.UseMesErrors();
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/internal/v1/telemetry/batches")
    {
        var feature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = 1048576;
        if (context.Request.ContentLength > 1048576)
        {
            context.Response.StatusCode = 413;
            return;
        }
    }
    await next(context);
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapServiceBasics("mes-backend");
app.MapGet("/health/ready", async (MesDbContext db, CancellationToken ct) =>
{
    try
    {
        return await db.Database.CanConnectAsync(ct)
            ? Results.Ok(
            new
            {
                status = "ready"
            }
        )
            : Results.StatusCode(503);
    }
    catch
    {
        return Results.StatusCode(503);
    }
});
app.MapPost("/internal/v1/auth/token", (TokenRequest request, ServiceTokenIssuer issuer) =>
{
    if (string.IsNullOrWhiteSpace(request.ClientId)
        || string.IsNullOrEmpty(request.ClientSecret)
        || string.IsNullOrWhiteSpace(request.Audience)
        || request.ClientId.Length > 64
        || request.ClientSecret.Length > 256
        || request.Audience.Length > 64)
        return Results.Unauthorized();
    var token = issuer.Issue(request);
    return token is null ? Results.Unauthorized() : Results.Ok(token);
})
    .AllowAnonymous()
    .RequireRateLimiting("auth");
app.MapGet("/api/v1/auth/csrf", (HttpContext context, IAntiforgery antiforgery) =>
{
    if (!context.Request.IsHttps)
        return Results.Problem(
            statusCode: 403,
            title: "HTTPS is required",
            extensions: new Dictionary<string, object?>
        {
            {
                "code",
                "HttpsRequired"
            }
        }
        );
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Mes-Time"] = DateTime.UtcNow.ToString(
        "O",
        System.Globalization.CultureInfo.InvariantCulture
    );
    return Results.Ok(new
    {
        requestToken = antiforgery.GetAndStoreTokens(context)
            .RequestToken
    });
});
async Task<bool> Csrf(HttpContext context, IAntiforgery antiforgery)
{
    if (!context.Request.IsHttps)
        return false;
    var origin = context.Request.Headers.Origin.ToString();
    if (origin.Length > 0
        && origin != (builder.Configuration["Security:PublicOrigin"]
        ?? "https://localhost:7443"))
        return false;
    try
    {
        await antiforgery.ValidateRequestAsync(context);
        return true;
    }
    catch (AntiforgeryValidationException)
    {
        return false;
    }
}

IResult CsrfDenied() =>
    Results.Problem(
        statusCode: 403,
        title: "Request verification failed",
        extensions: new Dictionary<string, object?>
{
    {
        "code",
        "CsrfFailed"
    }
}
    );
app.MapPost(
    "/api/v1/auth/login",
    async (
        LoginRequest request,
        HttpContext context,
        IAntiforgery antiforgery,
        UserAuthentication authentication,
        CancellationToken ct
    ) =>
{
    if (!await Csrf(context, antiforgery))
        return CsrfDenied();
    var session = await authentication.LoginAsync(request, ct);
    if (session is null)
        return Results.Problem(
            statusCode: 401,
            title: "Invalid credentials",
            extensions: new Dictionary<string, object?>
        {
            {
                "code",
                "InvalidCredentials"
            }
        }
        );
    await context.SignInAsync(
        SessionCookies.Scheme,
        session.Principal(),
        new AuthenticationProperties
    {
        IsPersistent =
        false,
        AllowRefresh =
        false
    }
    );
    return Results.Ok(session.Response());
}
)
    .AllowAnonymous()
    .RequireRateLimiting("auth");
app.MapGet(
    "/api/v1/auth/me",
    async (
        HttpContext context,
        UserAuthentication authentication,
        CancellationToken ct
    ) =>
{
    var session = await authentication.ValidateAsync(context.User, ct);
    return session is null ? Results.Unauthorized() : Results.Ok(session.Response());
}
)
    .RequireAuthorization(
    new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
{
    AuthenticationSchemes =
    SessionCookies.Scheme
}
);
app.MapPost("/api/v1/auth/logout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    if (!await Csrf(context, antiforgery))
        return CsrfDenied();
    await context.SignOutAsync(SessionCookies.Scheme);
    return Results.NoContent();
})
    .RequireAuthorization(
    new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
{
    AuthenticationSchemes =
    SessionCookies.Scheme
}
);
app.MapPost(
    "/api/v1/auth/password",
    async (
        PasswordChange request,
        HttpContext context,
        IAntiforgery antiforgery,
        UserAuthentication authentication,
        CancellationToken ct
    ) =>
{
    if (!await Csrf(context, antiforgery))
        return CsrfDenied();
    if (!await authentication.ChangePasswordAsync(
        context.User,
        request,
        ct
    ))
        return Results.BadRequest();
    await context.SignOutAsync(SessionCookies.Scheme);
    return Results.NoContent();
}
)
    .RequireAuthorization(
    new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
{
    AuthenticationSchemes =
    SessionCookies.Scheme
}
)
    .RequireRateLimiting("auth");
app.MapGet(
    "/api/v1/execution-policy",
    () => Results.Ok(new
{
    cloudAiEnabled = false,
    externalDeliveryEnabled = false,
    defaultAiProvider = "MOCK",
    note = "Local/Mock scaffold only"
})
)
    .RequireAuthorization("Admin");
app.MapOrders();
app.MapCollectionInternal();
app.MapAlarmInternal();
app.MapDeliveryInternal();
app.MapReportingInternal();
app.MapAnalysisInternal();
app.Run();


