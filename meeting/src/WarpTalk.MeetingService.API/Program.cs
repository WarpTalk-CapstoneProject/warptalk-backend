using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Application.Services;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Data;
using WarpTalk.MeetingService.Infrastructure.Extensions;
using WarpTalk.MeetingService.Infrastructure.Repositories;
using WarpTalk.MeetingService.Infrastructure.Services;
using WarpTalk.Shared.Extensions;
using WarpTalk.Shared.Grpc;
using WarpTalk.Shared.Protos;

AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWarpTalkObservability(
    builder.Configuration,
    builder.Environment,
    "warptalk-meeting");

builder.WebHost.ConfigureKestrel(options =>
{
    // HTTP/1-only port for REST API Gateway
    options.ListenAnyIP(5105, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1;
    });

    // HTTP/2-only port for gRPC
    options.ListenAnyIP(50055, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
    });
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

var redisConnectionString = builder.Configuration["Redis:ConnectionString"];
if (!string.IsNullOrEmpty(redisConnectionString))
{
    // This block is already conditional because Redis is optional for MeetingService; a
    // configured-but-unreachable Redis must therefore degrade the same way an unconfigured
    // one does, rather than killing the process. Registered as a factory (it used to
    // Connect() eagerly here) with abortConnect=false so the multiplexer reconnects itself.
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(
        _ => StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnectionString + ",abortConnect=false"));
    builder.Services.AddSingleton<IRedisService, RedisService>();
}

var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(builder.Configuration.GetConnectionString("DefaultConnection"));
var dataSource = dataSourceBuilder.Build();

var allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:3000", "http://localhost:3001", "http://localhost:5173", "https://warptalk.vn", "https://admin.warptalk.vn"];

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

builder.Services.AddDbContext<MeetingDbContext>(options =>
    options.UseNpgsql(dataSource));
builder.Services.AddWarpTalkServiceHealthChecks<MeetingDbContext>(
    "meeting-database");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwtSecret = builder.Configuration["Jwt:Secret"] ?? string.Empty;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(jwtSecret)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };

        // SignalR: Extract JWT from query string for WebSocket handshake
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                // SignalR can't attach an Authorization header, and the chat file download link is
                // opened as a plain <a href> (no header either) — both fall back to a query-string token.
                var isChatHub = path.StartsWithSegments("/api/v1/meetings/chat-hub");
                var isChatFileDownload = path.Value != null && path.Value.Contains("/chat/files/") && path.Value.EndsWith("/download");
                if (!string.IsNullOrEmpty(accessToken) && (isChatHub || isChatFileDownload))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddSignalR();


builder.Services.AddScoped<ILiveKitTokenService, LiveKitTokenService>();
builder.Services.AddHttpClient<ILiveKitEgressService, LiveKitEgressService>();
builder.Services.AddHttpClient<ILiveKitRoomAdminService, LiveKitRoomAdminService>();
builder.Services.AddScoped<ITranslationRoomGrpcService, TranslationRoomGrpcService>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<IMeetingRoomService, MeetingRoomService>();

// Polls + Q&A
builder.Services.AddScoped<IPollsService, PollsService>();
builder.Services.AddScoped<IQuestionsService, QuestionsService>();

// Breakout rooms (scoped-down)
builder.Services.AddScoped<IBreakoutsService, BreakoutsService>();

// WT-08: elects a new host when the Gateway's TranslationRoomHub signals a participant went
// fully offline (see MeetingRoomService.HandleHostOfflineAsync for why this is the sole
// authoritative election path).
builder.Services.AddHostedService<WarpTalk.MeetingService.API.Workers.HostFallbackConsumerWorker>();

// Applies WarpBot's answer to an @mention: reads assistant:chat_results, writes the assistant
// message, broadcasts it to the room.
//
// This was NEVER REGISTERED. The class has existed the whole time — consumer group, retries,
// dead-letter stream, a guarded XGROUP — and nothing ever started it, so every @WarpBot
// mention was published to the AI worker, answered by it, and then dropped on the floor. On
// production the meeting-chat-consumers group sat 41 entries behind with zero pending: not
// stuck, simply never read.
builder.Services.AddHostedService<WarpTalk.MeetingService.API.HostedServices.MeetingChatAssistantResultConsumerService>();

// Also never registered, and found by the same test: without it a breakout session's end time
// is a number in a row nobody acts on, so breakout rooms simply never expire. The worker is a
// guarded ten-second scan that only touches sessions already past due.
builder.Services.AddHostedService<WarpTalk.MeetingService.API.Workers.BreakoutExpiryWorker>();

// WT-371 #8: recording's ONLY completion path was LiveKit's egress_ended webhook, and on
// production that webhook was never configured — so every recording started, ran, uploaded, and
// was never heard from again, with the room left saying "recording" indefinitely. This sweep asks
// LiveKit directly. With the webhook working it finds nothing.
builder.Services.AddHostedService<WarpTalk.MeetingService.API.Workers.EgressReconciliationWorker>();

// Chat repositories and services
builder.Services.AddScoped<IMeetingChatMessageRepository, MeetingChatMessageRepository>();
builder.Services.AddScoped<IMeetingChatTranslationRepository, MeetingChatTranslationRepository>();
builder.Services.AddScoped<IMeetingChatAssistantRequestRepository, MeetingChatAssistantRequestRepository>();
builder.Services.AddScoped<IMeetingChatModerationEventRepository, MeetingChatModerationEventRepository>();
builder.Services.AddScoped<IMeetingChatNotifier, WarpTalk.MeetingService.API.Services.MeetingChatNotifier>();
builder.Services.AddScoped<IMeetingChatService, MeetingChatService>();
// WT-330: chat file storage must be selected from Storage:* configuration, not hard-wired.
// AddMeetingChatFileStorage registers the S3/MinIO adapter when Storage:Provider is
// S3-compatible and fails fast outside Development otherwise, so production can never
// fall back to local-disk writes that the non-root container cannot perform.
builder.Services.AddMeetingChatFileStorage(builder.Configuration, builder.Environment);
builder.Services.AddHttpClient<IChatTranslator, OpenAIChatTranslator>();

// History service
builder.Services.AddScoped<IMeetingHistoryService, MeetingHistoryService>();

// Shared by the egress_ended webhook and the reconciliation sweep, so "what finishing a recording
// means" exists in exactly one place — see IEgressCompletion.
builder.Services.AddScoped<IEgressCompletion, EgressCompletion>();
builder.Services.AddScoped<IEgressReconciliation, EgressReconciliationService>();
builder.Services.AddScoped<IMeetingWebhookService, MeetingWebhookService>();

builder.Services.AddGrpcClient<TranslationRoomService.TranslationRoomServiceClient>(o =>
{
    var url = builder.Configuration["GrpcUrls:TranslationRoomService"];
    if (string.IsNullOrEmpty(url)) throw new Exception("GrpcUrls:TranslationRoomService is missing in configuration.");
    o.Address = new Uri(url);
})
.AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);

builder.Services.AddGrpcClient<BillingService.BillingServiceClient>(o =>
{
    var url = builder.Configuration["GrpcUrls:BillingService"];
    if (string.IsNullOrEmpty(url)) throw new Exception("GrpcUrls:BillingService is missing in configuration.");
    o.Address = new Uri(url);
})
.AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<WarpTalk.MeetingService.API.Hubs.MeetingChatHub>("/api/v1/meetings/chat-hub");
app.MapWarpTalkServiceHealthChecks();

app.Run();
