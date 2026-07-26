using Npgsql;
using Npgsql.NameTranslation;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.API.GrpcServices;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;
using WarpTalk.TranslationRoomService.Infrastructure.Repositories;
using WarpTalk.Shared.Protos;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using FluentValidation;
using FluentValidation.AspNetCore;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.API.Extensions;
using WarpTalk.TranslationRoomService.API.Validators;
using TranslationRoomAppService = WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.StateMachines;
using WarpTalk.TranslationRoomService.Application.EventHandlers;
using WarpTalk.TranslationRoomService.Application.BackgroundProcessors;
using WarpTalk.TranslationRoomService.Infrastructure.BackgroundProcessors;
using WarpTalk.TranslationRoomService.Application.LanguagePolicy;
using WarpTalk.TranslationRoomService.API.Workers;
using WarpTalk.TranslationRoomService.Infrastructure.Redis;
using StackExchange.Redis;

AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    // HTTP/1-only port for REST API Gateway
    options.ListenAnyIP(5102, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1;
    });

    // HTTP/2-only port for gRPC
    options.ListenAnyIP(50052, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
    });
});

var dataSourceBuilder = new NpgsqlDataSourceBuilder(builder.Configuration.GetConnectionString("TranslationRoomDb"));
var dataSource = dataSourceBuilder.Build();


builder.Services.AddDbContext<TranslationRoomDbContext>(options =>
{
    options.UseNpgsql(dataSource);
    options.EnableSensitiveDataLogging();
    options.EnableDetailedErrors();
});

builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
builder.Services.AddScoped<ITranslationRoomRepository, TranslationRoomRepository>();
builder.Services.AddScoped<ITranslationRoomParticipantRepository, TranslationRoomParticipantRepository>();
builder.Services.AddScoped<ITranslationRoomAudioRouteRepository, TranslationRoomAudioRouteRepository>();
builder.Services.AddScoped<ITranslationRoomArtifactRepository, TranslationRoomArtifactRepository>();
builder.Services.AddScoped<ITranslationRoomSessionRepository, TranslationRoomSessionRepository>();
builder.Services.AddScoped<ITranslationRoomService, TranslationRoomAppService>();
builder.Services.AddScoped<ITranslationRoomArtifactService, TranslationRoomArtifactService>();
builder.Services.AddScoped<ITranslationRoomParticipantService, TranslationRoomParticipantService>();
builder.Services.AddScoped<ITranslationRoomAudioRouteService, TranslationRoomAudioRouteService>();
builder.Services.AddScoped<ITranslationRoomSessionService, TranslationRoomSessionService>();
builder.Services.AddScoped<IAudioRouteCacheService, AudioRouteCacheService>();
builder.Services.AddSingleton<IAudioRouteStateMachine, AudioRouteStateMachine>();
builder.Services.AddScoped<IAudioRouteTransitionProcessor, AudioRouteTransitionProcessor>();
builder.Services.AddScoped<IAudioRouteEventProcessor, AudioRouteEventProcessor>();
builder.Services.AddScoped<ITelemetryStateService, TelemetryStateService>();
builder.Services.AddScoped<ITelemetryProcessor, TelemetryProcessor>();
builder.Services.AddScoped<IArtifactsFinalizer, ArtifactsFinalizer>();
builder.Services.AddScoped<IRedisStateRepository, RedisStateRepository>();
builder.Services.AddSingleton<IRedisStreamRepository, RedisStreamRepository>();
builder.Services.AddScoped<ITranscriptCacheService, TranscriptCacheService>();
builder.Services.AddSingleton<IArtifactsFinalizationQueue, ArtifactsFinalizationQueue>();
builder.Services.AddHostedService<ArtifactsFinalizationWorker>();
builder.Services.AddHostedService<ArtifactsRecoveryWorker>();
builder.Services.AddHostedService<ParticipantOfflineConsumerWorker>();
// IdleRoomMonitoringWorker supersedes MeetingLifecycleWorker (removed): both scanned
// on the same 1-min/5-min cadence for the same ghost/idle rooms, but MeetingLifecycleWorker
// ended rooms via a raw entity update that skipped participant disconnection and the
// WT-67 audio-routing session_ends event — this worker ends rooms via the proper
// EndTranslationRoomAsync service method, which does both correctly.
builder.Services.AddHostedService<IdleRoomMonitoringWorker>();
builder.Services.AddHostedService<WorkspaceEventConsumerWorker>();
// WT-14: reminds the host/participants at T-10min and T-1min before a SCHEDULED room's start.
builder.Services.AddHostedService<ReminderNotificationWorker>();
builder.Services.AddScoped<ILanguageRepository, LanguageRepository>();
builder.Services.AddScoped<ILanguagePolicy, LanguagePolicy>();
builder.Services.AddScoped<IUserSettingsRepository, UserSettingsRepository>();
builder.Services.Configure<WarpTalk.TranslationRoomService.Domain.Configuration.AppSettings>(builder.Configuration.GetSection("App"));
builder.Services.Configure<WarpTalk.Shared.Configuration.SmtpSettings>(builder.Configuration.GetSection("Smtp"));
builder.Services.AddScoped<WarpTalk.Shared.Interfaces.IEmailService, WarpTalk.Shared.Services.SmtpEmailService>();

builder.Services.Configure<WarpTalk.TranslationRoomService.Domain.Configuration.TelemetrySettings>(
    builder.Configuration.GetSection("Telemetry"));

builder.Services.Configure<WarpTalk.TranslationRoomService.Domain.Configuration.ArtifactFinalizationSettings>(
    builder.Configuration.GetSection("ArtifactFinalization"));

// --- Redis ---
var redisConnectionString = builder.Configuration["Redis:ConnectionString"] 
                          ?? throw new InvalidOperationException("Redis:ConnectionString is not configured");
builder.Services.AddSingleton<IConnectionMultiplexer>(sp => 
    ConnectionMultiplexer.Connect(redisConnectionString));

// Hosted Services
builder.Services.AddHostedService<TranslationRoomEventConsumerService>();
builder.Services.AddHostedService<TelemetryRedisSubscriber>();

// Register FluentValidation Validators
builder.Services.AddValidatorsFromAssemblyContaining<CreateTranslationRoomRequestValidator>();
builder.Services.AddFluentValidationAutoValidation();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Secret"] ?? "CHANGE_ME_SUPER_SECRET_KEY_MIN_32_CHARS_LONG!!"))
        };
        options.Events = new JwtBearerEvents
        {
            // WT-14: /calendar.ics is opened as a plain link (calendar app, new browser tab)
            // that cannot attach an Authorization header, so fall back to "?access_token=" —
            // only when no header was already supplied, so normal API calls are unaffected.
            OnMessageReceived = context =>
            {
                if (string.IsNullOrEmpty(context.Token))
                {
                    var accessToken = context.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(accessToken))
                    {
                        context.Token = accessToken;
                    }
                }
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddGrpcClient<UserService.UserServiceClient>(o =>
{
    o.Address = new Uri(builder.Configuration["GrpcSettings:AuthServiceUrl"] ?? "http://localhost:5101");
});
builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.WorkspaceService.WorkspaceServiceClient>(o =>
{
    o.Address = new Uri(builder.Configuration["GrpcSettings:WorkspaceServiceUrl"] ?? "http://localhost:50056");
});
builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.TranscriptService.TranscriptServiceClient>(o =>
{
    o.Address = new Uri(builder.Configuration["GrpcSettings:TranscriptServiceUrl"] ?? "http://localhost:50055");
});
// WT-14: reused by ReminderNotificationWorker to push reminder notifications through the
// same NotificationService gRPC path other services use (see NotificationGrpcServiceImpl.SendNotification).
builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient>(o =>
{
    o.Address = new Uri(builder.Configuration["GrpcSettings:NotificationServiceUrl"] ?? "http://localhost:50054");
});


builder.Services.AddControllers();
builder.Services.AddCustomApiBehavior();

builder.Services.AddScoped<WarpTalk.TranslationRoomService.API.Filters.RateLimitingFilter>();

builder.Services.AddOpenApi();

builder.Services.AddGrpc();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGrpcService<TranslationRoomGrpcService>();

app.Run();
//for integration test only
public partial class Program { }
