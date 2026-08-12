using Npgsql;
using Npgsql.NameTranslation;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.API.GrpcServices;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;
using WarpTalk.TranslationRoomService.Infrastructure.Repositories;
using WarpTalk.TranslationRoomService.Infrastructure.Clients;
using WarpTalk.TranslationRoomService.Infrastructure.Adapters;
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
using WarpTalk.TranslationRoomService.Infrastructure.Storage;
using StackExchange.Redis;
using WarpTalk.Shared.Extensions;
using WarpTalk.Shared.Grpc;

AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.RequirePublicBaseUrl(builder.Environment, "App:FrontendBaseUrl");
builder.Services.AddWarpTalkObservability(
    builder.Configuration,
    builder.Environment,
    "warptalk-translation-room");

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
    if (builder.Environment.IsDevelopment())
    {
        options.EnableSensitiveDataLogging();
    }
    options.EnableDetailedErrors();
});
builder.Services.AddWarpTalkServiceHealthChecks<TranslationRoomDbContext>(
    "translation-room-database");

builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
builder.Services.AddScoped<ITranslationRoomRepository, TranslationRoomRepository>();
builder.Services.AddScoped<ITranslationRoomParticipantRepository, TranslationRoomParticipantRepository>();
builder.Services.AddScoped<ITranslationRoomAudioRouteRepository, TranslationRoomAudioRouteRepository>();
builder.Services.AddScoped<ITranslationRoomArtifactRepository, TranslationRoomArtifactRepository>();
builder.Services.AddScoped<ITranslationRoomSessionRepository, TranslationRoomSessionRepository>();
builder.Services.AddScoped<ITranslationRoomInvitationRepository, TranslationRoomInvitationRepository>();
builder.Services.AddScoped<ITranslationRoomFeedbackRepository, TranslationRoomFeedbackRepository>();
// WT-327: recurring bookings. Repository-per-entity, like every other repository above — there
// is no generic on IUnitOfWork and no Repository<T>() factory.
builder.Services.AddScoped<ITranslationRoomSeriesRepository, TranslationRoomSeriesRepository>();
builder.Services.AddScoped<ITranslationRoomService, TranslationRoomAppService>();
builder.Services.AddScoped<ITranslationRoomSeriesService, TranslationRoomSeriesService>();
builder.Services.AddScoped<ITranslationRoomArtifactService, TranslationRoomArtifactService>();
builder.Services.AddSingleton<IArtifactUrlSigner, S3ArtifactUrlSigner>();
builder.Services.AddScoped<ITranslationRoomParticipantService, TranslationRoomParticipantService>();
builder.Services.AddScoped<ITranslationRoomDirectoryService, TranslationRoomDirectoryService>();
builder.Services.AddScoped<ITranslationRoomAudioRouteService, TranslationRoomAudioRouteService>();
builder.Services.AddScoped<ITranslationRoomSessionService, TranslationRoomSessionService>();
builder.Services.AddScoped<IRecordingCompletedEventProcessor, RecordingCompletedEventProcessor>();
builder.Services.AddScoped<IRecordingCompletedStreamMessageHandler, RecordingCompletedStreamMessageHandler>();
builder.Services.AddScoped<IAudioRouteCacheService, AudioRouteCacheService>();
builder.Services.AddSingleton<IAudioRouteStateMachine, AudioRouteStateMachine>();
builder.Services.AddScoped<IAudioRouteTransitionProcessor, AudioRouteTransitionProcessor>();
builder.Services.AddScoped<IAudioRouteEventProcessor, AudioRouteEventProcessor>();
builder.Services.AddScoped<ITelemetryStateService, TelemetryStateService>();
builder.Services.AddScoped<ITelemetryProcessor, TelemetryProcessor>();
builder.Services.AddScoped<IArtifactsFinalizer, ArtifactsFinalizer>();
// Hands a finished meeting summary to warptalk-ai's KnowledgeFactWorker so it reaches the
// workspace knowledge index. Scoped because it resolves the workspace's AI policy over gRPC
// per call rather than caching it.
builder.Services.AddScoped<IKnowledgeFactRequestPublisher, RedisKnowledgeFactRequestPublisher>();
builder.Services.AddScoped<IRedisStateRepository, RedisStateRepository>();
builder.Services.AddSingleton<IRedisStreamRepository, RedisStreamRepository>();
builder.Services.AddScoped<ITranscriptCacheService, TranscriptCacheService>();
builder.Services.AddSingleton<IArtifactsFinalizationQueue, ArtifactsFinalizationQueue>();
builder.Services.AddHostedService<ArtifactsFinalizationWorker>();
builder.Services.AddHostedService<ArtifactsRecoveryWorker>();
builder.Services.AddHostedService<ParticipantOfflineConsumerWorker>();
// The other half of a summary rewrite. Without this the request reaches the AI worker and
// its answer is published to a stream nobody reads.
builder.Services.AddHostedService<SummaryResultConsumerWorker>();
// IdleRoomMonitoringWorker supersedes MeetingLifecycleWorker (removed): both scanned
// on the same 1-min/5-min cadence for the same ghost/idle rooms, but MeetingLifecycleWorker
// ended rooms via a raw entity update that skipped participant disconnection and the
// WT-67 audio-routing session_ends event — this worker ends rooms via the proper
// EndTranslationRoomAsync service method, which does both correctly.
builder.Services.AddHostedService<IdleRoomMonitoringWorker>();
builder.Services.AddHostedService<WorkspaceEventConsumerWorker>();
// WT-14: reminds the host/participants at T-10min and T-1min before a SCHEDULED room's start.
builder.Services.AddHostedService<ReminderNotificationWorker>();
// WT-327: rolls each recurring booking's horizon forward. Polling, not a Redis subscriber —
// "a day passed" is a clock fact, and an unguarded SubscribeAsync takes down the host process.
builder.Services.AddHostedService<RecurringSeriesMaterializationWorker>();
builder.Services.AddScoped<ILanguageRepository, LanguageRepository>();
builder.Services.AddScoped<ILanguagePolicy, LanguagePolicy>();
builder.Services.AddScoped<IUserSettingsDirectory, UserSettingsGrpcDirectory>();
builder.Services.AddScoped<IVoiceConsentDirectory, VoiceConsentGrpcDirectory>();
builder.Services.AddScoped<IWorkspaceMemberDirectory, WorkspaceMemberGrpcDirectory>();
builder.Services.AddScoped<IWorkspaceMeetingPolicy, WorkspaceMeetingPolicyGrpcClient>();
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
// abortConnect=false: room CRUD and the gRPC surface are Postgres-backed; Redis is the event
// bus. Safe here specifically because the two Redis-backed *gates* fail CLOSED rather than
// open — SubscriptionQuotaInterceptor and RateLimitingFilter let the RedisConnectionException
// surface, so the guarded call is rejected rather than silently allowed. If either is ever
// changed to catch-and-allow, this line becomes a quota bypass and must be revisited.
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect(redisConnectionString + ",abortConnect=false"));

// Hosted Services
builder.Services.AddHostedService<TranslationRoomEventConsumerService>();
builder.Services.AddHostedService<RecordingCompletedEventConsumerService>();
builder.Services.AddHostedService<TelemetryRedisSubscriber>();

// Register FluentValidation Validators
builder.Services.AddValidatorsFromAssemblyContaining<CreateTranslationRoomRequestValidator>();
builder.Services.AddFluentValidationAutoValidation();

builder.Services.AddWarpTalkJwtAuthentication(
    builder.Configuration,
    builder.Environment,
    options =>
    {
        options.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
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
    o.Address = builder.Configuration.GetRequiredServiceUri(
        builder.Environment,
        "GrpcSettings:AuthServiceUrl",
        "http://localhost:5101");
})
.AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);
builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.TranscriptService.TranscriptServiceClient>(o =>
{
    o.Address = builder.Configuration.GetRequiredServiceUri(
        builder.Environment,
        "GrpcSettings:TranscriptServiceUrl",
        "http://localhost:50055");
})
.AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);
// WT-188: resolves the caller's workspace role so a workspace Owner/Admin can admit participants
// into rooms they do not personally host (see TranslationRoomParticipantService.AdmitParticipantAsync).
builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.WorkspaceService.WorkspaceServiceClient>(o =>
{
    o.Address = builder.Configuration.GetRequiredServiceUri(
        builder.Environment,
        "GrpcSettings:WorkspaceServiceUrl",
        "http://localhost:50056");
})
.AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);
// WT-14: reused by ReminderNotificationWorker to push reminder notifications through the
// same NotificationService gRPC path other services use (see NotificationGrpcServiceImpl.SendNotification).
builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient>(o =>
{
    o.Address = builder.Configuration.GetRequiredServiceUri(
        builder.Environment,
        "GrpcSettings:NotificationServiceUrl",
        "http://localhost:50054");
})
.AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);

builder.Services.AddControllers();
builder.Services.AddCustomApiBehavior();

builder.Services.AddScoped<WarpTalk.TranslationRoomService.API.Filters.RateLimitingFilter>();

builder.Services.AddOpenApi();

builder.Services.AddWarpTalkGrpcServer(builder.Configuration, builder.Environment);

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
app.MapWarpTalkServiceHealthChecks();

app.Run();
//for integration test only
public partial class Program { }
