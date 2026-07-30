using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using StackExchange.Redis;
using System.Net;
using System.Threading.RateLimiting;
using WarpTalk.Gateway.Configuration;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Gateway.Middleware;
using WarpTalk.Gateway.Services;
using WarpTalk.Gateway.Transforms;
using WarpTalk.Shared.Extensions;
using WarpTalk.Shared.Grpc;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWarpTalkObservability(
    builder.Configuration,
    builder.Environment,
    "warptalk-gateway");

// 1. Configure JWT Authentication
builder.Services.AddWarpTalkJwtAuthentication(
    builder.Configuration,
    builder.Environment,
    options =>
    {
        options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;

        // SignalR: Extract JWT from query string for WebSocket handshake.
        // Browsers cannot send Authorization headers during WebSocket upgrade requests,
        // so the client passes the token as ?access_token=<jwt> query parameter.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];

                // Only extract from query string for Hub paths
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) &&
                    (path.StartsWithSegments("/hubs") ||
                     path.Value?.Contains("chat-hub", StringComparison.OrdinalIgnoreCase) == true ||
                     path.Value?.Contains("hub", StringComparison.OrdinalIgnoreCase) == true))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("RequireAuth", policy => policy.RequireAuthenticatedUser());
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 2;
    options.RequireHeaderSymmetry = true;

    foreach (var value in builder.Configuration
        .GetSection("ForwardedHeaders:KnownProxies")
        .Get<string[]>() ?? [])
    {
        if (!IPAddress.TryParse(value, out var address))
        {
            throw new InvalidOperationException(
                $"ForwardedHeaders:KnownProxies contains invalid IP address '{value}'.");
        }

        options.KnownProxies.Add(address);
    }

    foreach (var value in builder.Configuration
        .GetSection("ForwardedHeaders:KnownNetworks")
        .Get<string[]>() ?? [])
    {
        if (!System.Net.IPNetwork.TryParse(value, out var network))
        {
            throw new InvalidOperationException(
                $"ForwardedHeaders:KnownNetworks contains invalid CIDR '{value}'.");
        }

        options.KnownIPNetworks.Add(network);
    }
});

// 2. Configure CORS (with configurable origins)
var allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()
    ?? ["https://warptalk.vn", "https://admin.warptalk.vn"];

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (builder.Environment.IsDevelopment())
        {
            policy.SetIsOriginAllowed(origin => true) // Allow ngrok dynamic URLs
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
        }
        else
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials(); // Required for SignalR
        }
    });
});

// 3. Configure Rate Limiting
var rateLimits = builder.Configuration
    .GetSection(GatewayRateLimitOptions.SectionName)
    .Get<GatewayRateLimitOptions>() ?? new GatewayRateLimitOptions();
rateLimits.Validate();

builder.Services.AddRateLimiter(options =>
{
    var ipLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: RequestRateLimitPartitionKeys.Ip(httpContext),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = rateLimits.IpPermitLimit,
                Window = TimeSpan.FromSeconds(rateLimits.WindowSeconds),
                QueueLimit = 0
            }));
    var userLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: RequestRateLimitPartitionKeys.User(httpContext),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = rateLimits.UserPermitLimit,
                Window = TimeSpan.FromSeconds(rateLimits.WindowSeconds),
                QueueLimit = 0
            }));
    var workspaceLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        var workspace = RequestRateLimitPartitionKeys.Workspace(httpContext);
        return workspace is null
            ? RateLimitPartition.GetNoLimiter("no-workspace")
            : RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: workspace,
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    AutoReplenishment = true,
                    PermitLimit = rateLimits.WorkspacePermitLimit,
                    Window = TimeSpan.FromSeconds(rateLimits.WindowSeconds),
                    QueueLimit = 0
                });
    });
    options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
        ipLimiter,
        userLimiter,
        workspaceLimiter);
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Specific policy for login
    options.AddFixedWindowLimiter("LoginPolicy", opt =>
    {
        opt.PermitLimit = rateLimits.LoginPermitLimit;
        opt.Window = TimeSpan.FromSeconds(rateLimits.WindowSeconds);
    });

    // Specific policy for inbox
    options.AddFixedWindowLimiter("InboxPolicy", opt =>
    {
        opt.PermitLimit = rateLimits.InboxPermitLimit;
        opt.Window = TimeSpan.FromSeconds(rateLimits.WindowSeconds);
    });
});

// 4. Configure YARP Reverse Proxy
builder.Services.AddTransient<Yarp.ReverseProxy.Transforms.Builder.ITransformProvider, InternalContextTransformProvider>();
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// 5. Configure SignalR
var signalRBuilder = builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
    options.MaximumReceiveMessageSize = 128 * 1024; // 128 KB — voice-cloned audio chunks
});

// Optional: Use Redis backplane for horizontal scaling
var redisConnectionString = builder.Configuration["SignalR:Redis"];
if (!string.IsNullOrEmpty(redisConnectionString))
{
    signalRBuilder.AddStackExchangeRedis(redisConnectionString, options =>
    {
        options.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("WarpTalk");
    });
}

// 6. Register Connection Manager (singleton — in-memory tracking)
builder.Services.AddSingleton<IConnectionManager, ConnectionManager>();

// 7. Configure Redis for AI pipeline streams
var redisStreamConnectionString = builder.Configuration["Redis:ConnectionString"];
if (string.IsNullOrWhiteSpace(redisStreamConnectionString))
{
    redisStreamConnectionString = redisConnectionString; // Fall back to SignalR Redis config
}
if (string.IsNullOrWhiteSpace(redisStreamConnectionString))
{
    if (builder.Environment.IsProduction())
    {
        throw new InvalidOperationException(
            "Redis:ConnectionString or SignalR:Redis must be configured in Production.");
    }

    redisStreamConnectionString = "localhost:6379";
}

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(redisStreamConnectionString));

builder.Services.AddSingleton<RedisStreamService>();
builder.Services.AddSingleton<ActiveTranslationRoomRegistry>();
builder.Services.AddHostedService<AiResultConsumerService>();
builder.Services.AddHostedService<NotificationRedisSubscriberService>();
builder.Services.AddHostedService<TranslationRoomRedisSubscriberService>();

// 8. Configure liveness separately from dependency-aware readiness.
builder.Services
    .AddHealthChecks()
    .AddCheck(
        "self",
        () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(),
        tags: ["live"])
    .AddWarpTalkRedisReadiness("gateway-redis");

// 9. Configure authenticated, deadline-bound and retryable internal gRPC clients.
builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient>(o =>
{
    o.Address = builder.Configuration.GetRequiredServiceUri(
        builder.Environment,
        "GrpcUrls:NotificationServiceUrl",
        "http://localhost:50054");
})
.AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);

builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.WorkspaceService.WorkspaceServiceClient>(o =>
{
    o.Address = builder.Configuration.GetRequiredServiceUri(
        builder.Environment,
        "GrpcUrls:WorkspaceServiceUrl",
        "http://localhost:50056");
})
.AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);

builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.TranslationRoomService.TranslationRoomServiceClient>(o =>
{
    o.Address = builder.Configuration.GetRequiredServiceUri(
        builder.Environment,
        "GrpcUrls:TranslationRoomServiceUrl",
        "http://localhost:50052");
})
.AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseForwardedHeaders();
app.UseWebSockets();
app.UseCors();

// Security Headers Middleware
// [Security] Set HTTP response headers to protect against XSS, clickjacking, and MIME-sniffing.
app.Use(async (context, next) => {
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next();
});

app.UseAuthentication();
app.UseMiddleware<SecurityAuditMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();

// Map YARP
app.MapReverseProxy();

// Map SignalR Hubs (JWT-protected)
app.MapHub<TranslationRoomHub>("/hubs/translation-room")
    .RequireAuthorization("RequireAuth");

app.MapHub<NotificationHub>("/hubs/notification")
    .RequireAuthorization("RequireAuth");



// Standard platform probes. Keep the two legacy aliases during the migration
// window so existing local tooling does not break.
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live")
});
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});
app.MapHealthChecks("/health");
app.MapHealthChecks("/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.Run();
