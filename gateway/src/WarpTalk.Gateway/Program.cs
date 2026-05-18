using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using System.Text;
using System.Threading.RateLimiting;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Gateway.Services;

var builder = WebApplication.CreateBuilder(args);

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
                    path.StartsWithSegments("/hubs"))
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

// 8. Configure Health Checks
builder.Services.AddHealthChecks();

// 9. Configure gRPC Clients & Server
builder.Services.AddGrpc();
builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient>(o =>
{
    o.Address = new Uri(builder.Configuration["ReverseProxy:Clusters:notification-cluster:Destinations:notification-service:Address"] ?? "http://localhost:5104");
})
.ConfigurePrimaryHttpMessageHandler(() =>
{
    var handler = new HttpClientHandler();
    if (builder.Environment.IsDevelopment())
    {
        // [Security] Bypass TLS verification ONLY in local development to fix trust certificate issues (T014).
        handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
    }
    return handler;
})
.AddCallCredentials((context, metadata, serviceProvider) =>
{
    // [Security] Zero-Trust Inter-service Authentication: Inject internal secret to gRPC requests.
    var config = serviceProvider.GetRequiredService<IConfiguration>();
    var env = serviceProvider.GetRequiredService<IWebHostEnvironment>();
    
    var rawGrpcSecret = config["Grpc:InternalSecret"];
    var isDefaultOrInvalid = string.IsNullOrWhiteSpace(rawGrpcSecret) || 
                             rawGrpcSecret.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase) ||
                             rawGrpcSecret.Length < 32;

    if (env.IsProduction() && isDefaultOrInvalid)
    {
        throw new InvalidOperationException("CRITICAL SECURITY: Grpc Internal Secret is not properly configured for Production. It must be at least 32 characters long and not be the default placeholder.");
    }

    var secret = isDefaultOrInvalid 
        ? "CHANGE_ME_INTERNAL_SECRET_MIN_32_CHARS_LONG!!" 
        : rawGrpcSecret!;
        
    metadata.Add("x-internal-token", secret);
    return Task.CompletedTask;
});

var app = builder.Build();

// Configure the HTTP request pipeline.
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



// Map Health Checks
app.MapHealthChecks("/health");
app.MapHealthChecks("/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.Run();
