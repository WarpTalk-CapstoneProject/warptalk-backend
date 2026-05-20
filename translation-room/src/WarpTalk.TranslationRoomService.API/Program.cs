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
builder.Services.AddScoped<ITranslationRoomService, TranslationRoomAppService>();
builder.Services.AddScoped<ITranslationRoomArtifactService, TranslationRoomArtifactService>();
builder.Services.AddScoped<ITranslationRoomParticipantService, TranslationRoomParticipantService>();
builder.Services.AddScoped<ITranslationRoomAudioRouteService, TranslationRoomAudioRouteService>();
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
builder.Services.AddScoped<ILanguageRepository, LanguageRepository>();
builder.Services.AddScoped<ILanguagePolicy, LanguagePolicy>();
builder.Services.AddScoped<IUserSettingsRepository, UserSettingsRepository>();

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
    });
builder.Services.AddAuthorization();
builder.Services.AddGrpcClient<UserService.UserServiceClient>(o =>
{
    o.Address = new Uri(builder.Configuration["GrpcSettings:AuthServiceUrl"] ?? "http://localhost:5101");
});
builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.TranscriptService.TranscriptServiceClient>(o =>
{
    o.Address = new Uri(builder.Configuration["GrpcSettings:TranscriptServiceUrl"] ?? "http://localhost:50055");
});

builder.Services.AddControllers();
builder.Services.AddCustomApiBehavior();
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
