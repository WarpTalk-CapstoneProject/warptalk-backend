using System.Net;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using StackExchange.Redis;
using WarpTalk.Shared.Protos;
using WarpTalk.TranscriptService.Application.Interfaces;
using WarpTalk.TranscriptService.Application.Services;
using WarpTalk.TranscriptService.Domain.Enums;
using WarpTalk.TranscriptService.Domain.Interfaces;
using WarpTalk.TranscriptService.Infrastructure.Persistence;
using WarpTalk.TranscriptService.Infrastructure.Persistence.Contexts;
using WarpTalk.TranscriptService.Infrastructure.Repositories;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Extensions;
using WarpTalk.Shared.Grpc;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWarpTalkObservability(
    builder.Configuration,
    builder.Environment,
    "warptalk-transcript");

builder.WebHost.ConfigureKestrel(options =>
{
    // HTTP/1-only port for REST API Gateway
    options.ListenAnyIP(5103, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1;
    });

    // HTTP/2-only port for gRPC
    options.ListenAnyIP(50053, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
    });
});

// --- DbContext ---
var dataSourceBuilder = new NpgsqlDataSourceBuilder(builder.Configuration.GetConnectionString("TranscriptDb"));
var dataSource = dataSourceBuilder.Build();

builder.Services.AddDbContext<TranscriptDbContext>(options =>
    options.UseNpgsql(dataSource));
builder.Services.AddWarpTalkServiceHealthChecks<TranscriptDbContext>(
    "transcript-database");

// --- Repositories ---
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));

// --- Application Services ---
builder.Services.AddScoped<ITranscriptCorrectionService, TranscriptCorrectionService>();
builder.Services.AddScoped<IGlossaryService, GlossaryService>();
builder.Services.AddScoped<IGlobalGlossaryService, GlobalGlossaryService>();
builder.Services.AddScoped<ITranscriptQueryService, TranscriptQueryService>();
builder.Services.AddScoped<ITranscriptExportService, TranscriptExportService>();

// --- Redis ---
var redisConnectionString = builder.Configuration["Redis:ConnectionString"]
                          ?? throw new InvalidOperationException("Redis:ConnectionString is not configured");
// abortConnect=false: transcript read/search/export are Postgres-backed and stay useful
// while the Redis ingest path is down. The consumers below retry their consumer groups with
// bounded backoff and pick up again once Redis returns, without a restart.
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect(redisConnectionString + ",abortConnect=false"));

builder.Services.AddHostedService<WarpTalk.TranscriptService.Infrastructure.Redis.TranscriptRedisConsumerService>();
builder.Services.AddHostedService<WarpTalk.TranscriptService.Infrastructure.Redis.GlossaryStartedEventConsumer>();

// --- Authentication ---
builder.Services.AddWarpTalkJwtAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddAuthorization();
builder.Services.AddWarpTalkSystemAdminAuthorization();

// --- gRPC Clients ---
builder.Services.AddGrpcClient<UserService.UserServiceClient>(o =>
{
    o.Address = new Uri(builder.Configuration["GrpcUrls:AuthServiceUrl"]!);
})
.AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);

builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.TranslationRoomService.TranslationRoomServiceClient>(o =>
{
    o.Address = new Uri(builder.Configuration["GrpcUrls:TranslationRoomServiceUrl"]!);
})
.AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);

builder.Services.AddGrpcClient<BillingService.BillingServiceClient>(o =>
{
    o.Address = builder.Configuration.GetRequiredServiceUri(
        builder.Environment,
        "GrpcUrls:BillingServiceUrl",
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

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
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

app.MapGrpcService<WarpTalk.TranscriptService.API.GrpcServices.TranscriptGrpcService>();
app.MapWarpTalkServiceHealthChecks();
app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client.");

app.Run();
