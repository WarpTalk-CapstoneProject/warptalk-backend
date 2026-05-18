using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
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

var builder = WebApplication.CreateBuilder(args);

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
dataSourceBuilder.MapEnum<TranscriptStatus>("transcript.transcript_status");
dataSourceBuilder.MapEnum<CorrectionStatus>("transcript.correction_status");
dataSourceBuilder.MapEnum<CorrectionType>("transcript.correction_type");
var dataSource = dataSourceBuilder.Build();

builder.Services.AddDbContext<TranscriptDbContext>(options =>
    options.UseNpgsql(dataSource));

// --- Repositories ---
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));

// --- Application Services ---
builder.Services.AddScoped<ITranscriptCorrectionService, TranscriptCorrectionService>();
builder.Services.AddScoped<IGlossaryService, GlossaryService>();
builder.Services.AddScoped<ITranscriptQueryService, TranscriptQueryService>();
builder.Services.AddScoped<ITranscriptExportService, TranscriptExportService>();

// --- Redis ---
var redisConnectionString = builder.Configuration["Redis:ConnectionString"] 
                          ?? throw new InvalidOperationException("Redis:ConnectionString is not configured");
builder.Services.AddSingleton<IConnectionMultiplexer>(sp => 
    ConnectionMultiplexer.Connect(redisConnectionString));

builder.Services.AddHostedService<WarpTalk.TranscriptService.Infrastructure.Redis.TranscriptRedisConsumerService>();

// --- Authentication ---
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

// --- gRPC Clients ---
builder.Services.AddGrpcClient<UserService.UserServiceClient>(o =>
{
    o.Address = new Uri(builder.Configuration["GrpcUrls:AuthServiceUrl"]!);
});

builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.TranslationRoomService.TranslationRoomServiceClient>(o =>
{
    o.Address = new Uri(builder.Configuration["GrpcUrls:TranslationRoomServiceUrl"]!);
});

builder.Services.AddGrpcClient<BillingService.BillingServiceClient>(o =>
{
    o.Address = new Uri(builder.Configuration["GrpcUrls:BillingServiceUrl"] ?? "http://localhost:50054");
});

builder.Services.AddControllers();
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

app.MapGrpcService<WarpTalk.TranscriptService.API.GrpcServices.TranscriptGrpcService>();
app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client.");

app.Run();
