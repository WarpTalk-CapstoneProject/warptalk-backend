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
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Coordination;
using WarpTalk.Shared.Extensions;
using WarpTalk.Gateway.Configuration;
using WarpTalk.Gateway.Constants;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Gateway.Monitoring;
using WarpTalk.Gateway.Presence;
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
//
// This goes through the shared helper deliberately. The gateway used to read Jwt:Secret itself
// and reject only null/empty, which meant it would boot in Production on the CHANGE_ME
// placeholder that is committed to appsettings.json in this public repository. The gateway is
// the only component that validates end-user JWTs on proxied routes, so a known signing key
// there is not a weak link — it is the whole lock: anyone could mint a token for any user id
// and any role and be believed. Every other service already refused to start in that state via
// AddWarpTalkJwtAuthentication; the perimeter was the one place that did not.
//
// Adopting the helper also restores PreviousSecrets support, so a key rotation that works for
// the backend services no longer breaks at the gateway. JwtKeyRotationTests already asserted
// that behaviour against the helper from inside this very test project, while the gateway's own
// Program.cs did not implement it.
builder.Services.AddWarpTalkJwtAuthentication(
    builder.Configuration,
    builder.Environment,
    options =>
    {
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

                // The embedded Grafana's ForwardAuth call, and nothing else: an iframe cannot
                // send an Authorization header, so the admin's access-token cookie stands in for
                // it on that one path. See GrafanaForwardAuth.
                if (string.IsNullOrEmpty(context.Token)
                    && WarpTalk.Gateway.Monitoring.GrafanaForwardAuth.TryReadCookieToken(context.Request, out var cookieToken))
                {
                    context.Token = cookieToken;
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("RequireAuth", policy => policy.RequireAuthenticatedUser());
});
// The system-admin gate for the embedded Grafana's ForwardAuth endpoint — the same policy every
// ~/api/v1/admin/* endpoint is behind in the services.
builder.Services.AddWarpTalkSystemAdminAuthorization();

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

// Limits come from the RateLimits configuration section (docker-compose passes RateLimits__*).
// They must never be written as literals here again — see GatewayRateLimiterExtensions.
builder.Services.AddWarpTalkGatewayRateLimiting(builder.Configuration);

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

// Redis backplane: REQUIRED as soon as there is more than one gateway replica. The relay
// subscribers (RealtimeRelay) and the AI result stream consumer group each hand an event to ONE
// pod and rely on the backplane to reach the clients held by the others. Reads SignalR:Redis and
// falls back to Redis:ConnectionString, so a deployment that forgets SignalR__Redis still scales
// correctly instead of silently delivering to 1/N of the clients. abortConnect=false: a backplane
// that cannot reach Redis degrades this instance to single-node SignalR rather than stopping the
// gateway from booting (same reason as the multiplexer below). Channel prefix unchanged.
//
// Negotiate -> connect: the gateway hubs are negotiated and connected through Traefik, whose
// sticky cookie (warptalk_gw_stick, deploy/k3s/chart/templates/ingress.yaml) pins both requests to
// one gateway pod. Long Polling also depends on that stickiness.
var backplaneRedis = SignalRBackplaneExtensions.ResolveBackplaneConnectionString(builder.Configuration);
if (backplaneRedis is not null)
{
    signalRBuilder.AddStackExchangeRedis(backplaneRedis, options =>
    {
        options.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("WarpTalk");
        options.Configuration.AbortOnConnectFail = false;
    });
}
var redisConnectionString = builder.Configuration["SignalR:Redis"];

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

// abortConnect=false is load-bearing, not tuning. Without it StackExchange.Redis throws
// out of this factory while the service provider is being built, i.e. before any
// BackgroundService exists to guard, and the process dies. The gateway's primary job is
// proxying HTTP and terminating SignalR; the whole API surface must keep answering when
// realtime is temporarily down. The multiplexer returned here is disconnected and
// reconnects on its own, and /health/ready reports the degradation (see
// AddWarpTalkRedisReadiness below) so nothing pretends to be healthy.
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(redisStreamConnectionString + ",abortConnect=false"));

builder.Services.AddSingleton<RedisStreamService>();
builder.Services.AddSingleton<ActiveTranslationRoomRegistry>();

// Member presence. Registered after the multiplexer above because it is Redis-backed rather
// than kept in the connection manager: the Members page has to read who is online outside the
// socket that produced it, and a second Gateway instance must not report only its own half.
builder.Services.AddSingleton<IPresenceStore, RedisPresenceStore>();
builder.Services.AddSingleton<IPresenceNotifier, PresenceNotifier>();
builder.Services.AddHostedService<PresenceHeartbeatService>();

// One gateway pod at a time relays pub/sub events into SignalR; see RealtimeRelay. Registered
// before the subscribers so the elector starts first and stops (releasing its lease) last.
builder.Services.AddWarpTalkPubSubLeadership(RealtimeRelay.LeaseResource, RealtimeRelay.RequiredSubscriptions);

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

// Server-side host check for TranslationRoomHub's host-only methods (MuteAll,
// SpotlightParticipant, AdmitWaitingParticipant). Registered after both gRPC clients above
// because it composes them: room host from TranslationRoomService, workspace Owner/Admin from
// WorkspaceService — the same two clauses the REST paths enforce.
builder.Services.AddScoped<WarpTalk.Gateway.Services.IRoomHostAuthority, WarpTalk.Gateway.Services.RoomHostAuthority>();
// Server-side check of the workspace's allowed-language policy for SetSpeakLanguage and
// SetListenLanguage. Composes the same two clients for the same reason: the room says which
// workspace it belongs to, the workspace says which languages it permits. Until this existed the
// policy was enforced only by the pickers in the web client.
builder.Services.AddScoped<WarpTalk.Gateway.Services.IRoomLanguagePolicy, WarpTalk.Gateway.Services.RoomLanguagePolicy>();
// WT-335: scoped, like RoomHostAuthority — it depends on the scoped WorkspaceServiceClient, and a
// singleton would also be the wrong lifetime for something that must never cache its answer.
builder.Services.AddScoped<IPresenceVisibility, PresenceVisibility>();
// The one presence snapshot both NotificationHub.QueryPresence and POST /api/v1/presence/query
// answer from. Scoped because it composes the scoped visibility check above.
builder.Services.AddScoped<IPresenceQueryService, PresenceQueryService>();

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

app.UseAuthentication();

// AFTER UseAuthentication, and this ordering is load-bearing. The global limiter partitions a
// signed-in caller by user id so that everyone behind one NAT — an office, a venue, a defence
// room — does not share a single budget. HttpContext.User is not populated until authentication
// has run, so a limiter placed above it would find no identity and silently partition every
// request by IP again, which is the exact failure it is there to prevent.
app.UseRateLimiter();

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

app.MapPresenceEndpoints();
app.MapGrafanaForwardAuth();



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

public partial class Program { }
