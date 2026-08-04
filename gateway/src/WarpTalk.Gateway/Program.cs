using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using System.Net;
using System.Text;
using System.Threading.RateLimiting;
using WarpTalk.Shared.Extensions;
using WarpTalk.Gateway.Constants;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Gateway.Services;
using WarpTalk.Gateway.Transforms;
using WarpTalk.Shared.Grpc;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWarpTalkObservability(
    builder.Configuration,
    builder.Environment,
    "warptalk-gateway");

// 1. Configure JWT Authentication
var jwtSettings = builder.Configuration.GetSection("Jwt");
var secretKey = jwtSettings["Secret"];

if (string.IsNullOrEmpty(secretKey))
{
    throw new InvalidOperationException("JWT Secret is not configured.");
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings["Issuer"],
            ValidAudience = jwtSettings["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey)),
            ClockSkew = TimeSpan.Zero
        };

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
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedHost;

    foreach (var proxy in builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
    {
        if (IPAddress.TryParse(proxy, out var address))
        {
            options.KnownProxies.Add(address);
        }
    }

    foreach (var network in builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
    {
        if (System.Net.IPNetwork.TryParse(network, out var parsedNetwork))
        {
            options.KnownIPNetworks.Add(parsedNetwork);
        }
    }
});

builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? httpContext.Request.Headers.Host.ToString(),
            factory: partition => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1)
            }));

    // Specific policy for login
    options.AddFixedWindowLimiter("LoginPolicy", opt =>
    {
        opt.PermitLimit = 5;
        opt.Window = TimeSpan.FromMinutes(1);
    });

    // Specific policy for inbox
    options.AddFixedWindowLimiter("InboxPolicy", opt =>
    {
        opt.PermitLimit = 30;
        opt.Window = TimeSpan.FromMinutes(1);
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
    redisStreamConnectionString = "localhost:6379";
}

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(redisStreamConnectionString));

builder.Services.AddSingleton<RedisStreamService>();
builder.Services.AddSingleton<ActiveTranslationRoomRegistry>();
builder.Services.AddHostedService<AiResultConsumerService>();
builder.Services.AddHostedService<NotificationRedisSubscriberService>();
builder.Services.AddHostedService<TranslationRoomRedisSubscriberService>();
builder.Services.AddHostedService<WarpTalk.Gateway.Services.BillingRedisSubscriberService>();

// 8. Configure Health Checks
builder.Services
    .AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddWarpTalkRedisReadiness("gateway-redis");

// 9. Configure gRPC Clients & Server
builder.Services.AddGrpc();
builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient>(o =>
{
    var address = builder.Configuration["GrpcUrls:NotificationServiceUrl"]
                  ?? "http://localhost:50054";
    o.Address = new Uri(address);
})
.AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);

builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.WorkspaceService.WorkspaceServiceClient>(o =>
{
    var address = builder.Configuration["GrpcUrls:WorkspaceServiceUrl"]
                  ?? "http://localhost:50056";
    o.Address = new Uri(address);
})
.AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);

builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.TranslationRoomService.TranslationRoomServiceClient>(o =>
{
    var address = builder.Configuration["GrpcUrls:TranslationRoomServiceUrl"]
                  ?? "http://localhost:50052";
    o.Address = new Uri(address);
})
.AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseWebSockets();
app.UseForwardedHeaders();
app.UseCors();

// Security Headers Middleware
// [Security] Set HTTP response headers to protect against XSS, clickjacking, and MIME-sniffing.
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next();
});

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

// Map YARP
app.MapReverseProxy();

// Map SignalR Hubs (JWT-protected)
app.MapHub<TranslationRoomHub>("/hubs/translation-room")
    .RequireAuthorization("RequireAuth");

app.MapHub<NotificationHub>("/hubs/notification")
    .RequireAuthorization("RequireAuth");

app.MapHub<WarpTalk.Gateway.Hubs.BillingHub>(RealtimeConstants.Billing.HubPath)
    .RequireAuthorization("RequireAuth");



// Map Health Checks
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live")
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.Run();
