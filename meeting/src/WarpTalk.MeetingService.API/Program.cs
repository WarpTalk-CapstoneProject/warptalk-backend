using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Application.Services;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Data;
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
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(
        StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnectionString));
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

// Chat repositories and services
builder.Services.AddScoped<IMeetingChatMessageRepository, MeetingChatMessageRepository>();
builder.Services.AddScoped<IMeetingChatTranslationRepository, MeetingChatTranslationRepository>();
builder.Services.AddScoped<IMeetingChatAssistantRequestRepository, MeetingChatAssistantRequestRepository>();
builder.Services.AddScoped<IMeetingChatModerationEventRepository, MeetingChatModerationEventRepository>();
builder.Services.AddScoped<IMeetingChatNotifier, WarpTalk.MeetingService.API.Services.MeetingChatNotifier>();
builder.Services.AddScoped<IMeetingChatService, MeetingChatService>();
builder.Services.AddScoped<IMeetingChatFileStorage, WarpTalk.MeetingService.Infrastructure.Storage.LocalMeetingChatFileStorage>();
builder.Services.AddHttpClient<IChatTranslator, OpenAIChatTranslator>();

// History service
builder.Services.AddScoped<IMeetingHistoryService, MeetingHistoryService>();

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

app.Run();
