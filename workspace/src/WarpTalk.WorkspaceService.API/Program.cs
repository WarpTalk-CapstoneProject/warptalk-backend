using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Interfaces.Caching;
using WarpTalk.WorkspaceService.Application.Services;
using WarpTalk.WorkspaceService.Application.Evaluators;
using WarpTalk.WorkspaceService.Domain.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Caching;
using WarpTalk.WorkspaceService.Infrastructure.Clients;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;
using WarpTalk.WorkspaceService.Infrastructure.Repositories;
using WarpTalk.Shared.Protos;
using StackExchange.Redis;
using WarpTalk.WorkspaceService.Infrastructure.Storage;
using WarpTalk.WorkspaceService.Infrastructure.BackgroundServices;
using WarpTalk.WorkspaceService.API.Providers;
using WarpTalk.WorkspaceService.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    // HTTP/1-only port for REST API Gateway
    options.ListenAnyIP(5106, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1;
    });

    // HTTP/2-only port for gRPC (optional / placeholder)
    options.ListenAnyIP(50056, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
    });
});

// Database
var connectionString = builder.Configuration.GetConnectionString("WorkspaceDb") 
                      ?? "Host=localhost;Database=warptalk;Username=postgres;Password=postgres;Search Path=workspace,public";
builder.Services.AddDbContext<WorkspaceDbContext>(options =>
{
    options.UseNpgsql(connectionString);
});

// Cache (Redis)
var redisConnectionString = builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379";
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = redisConnectionString;
});
builder.Services.AddSingleton<IConnectionMultiplexer>(sp => ConnectionMultiplexer.Connect(redisConnectionString));
builder.Services.AddScoped<IWorkspaceCacheService, WorkspaceCacheService>();

// Auth identity (gRPC → UserService in WarpTalk.Shared, implemented by UserServiceGrpc in Auth API)
builder.Services.AddGrpcClient<UserService.UserServiceClient>(o =>
{
    o.Address = new Uri(builder.Configuration["GrpcSettings:AuthServiceUrl"] ?? "http://localhost:50051");
});
builder.Services.AddScoped<IAuthIdentityClient, AuthIdentityGrpcClient>();

// Translation Room Service (gRPC)
builder.Services.AddGrpcClient<TranslationRoomService.TranslationRoomServiceClient>(o =>
{
    o.Address = new Uri(builder.Configuration["GrpcSettings:TranslationRoomServiceUrl"] ?? "http://localhost:50052");
});
builder.Services.AddScoped<ITranslationRoomClient, TranslationRoomGrpcClient>();

// Repositories & Unit of Work
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
builder.Services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
builder.Services.AddScoped<IWorkspaceMemberRepository, WorkspaceMemberRepository>();
builder.Services.AddScoped<IWorkspaceInvitationRepository, WorkspaceInvitationRepository>();
// Services
builder.Services.AddScoped<IWorkspaceService, WarpTalk.WorkspaceService.Application.Services.WorkspaceService>();
builder.Services.AddScoped<IWorkspaceMemberService, WorkspaceMemberService>();
builder.Services.AddScoped<IWorkspaceInvitationService, WarpTalk.WorkspaceService.Application.Services.WorkspaceInvitationService>();
builder.Services.AddScoped<IWorkspaceDocumentService, WorkspaceDocumentService>();
builder.Services.AddScoped<IDocumentTextExtractor, DocumentTextExtractor>();
builder.Services.AddScoped<IDocumentSecurityScanner, DocumentSecurityScanner>();
builder.Services.AddScoped<IDocumentAccessEvaluator, DocumentAccessEvaluator>();
builder.Services.AddScoped<IWorkspaceDocumentEventPublisher, RedisDocumentEventPublisher>();
builder.Services.AddSingleton<IWorkspaceDocumentStorage, LocalEncryptedWorkspaceDocumentStorage>();
builder.Services.AddHttpClient();
builder.Services.AddHostedService<DocumentSecurityGuardrailConsumerService>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IWorkspaceUrlProvider, WorkspaceUrlProvider>();

builder.Services.AddControllers();

var rawJwtSecret = builder.Configuration["Jwt:Secret"];
var isDefaultOrInvalid = string.IsNullOrWhiteSpace(rawJwtSecret) || 
                         rawJwtSecret.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase) ||
                         rawJwtSecret.Length < 32;

var validatedSecret = isDefaultOrInvalid 
    ? "CHANGE_ME_SUPER_SECRET_KEY_MIN_32_CHARS_LONG!!" 
    : rawJwtSecret;

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
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(validatedSecret!))
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddGrpc();

var app = builder.Build();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGrpcService<WarpTalk.WorkspaceService.API.GrpcServices.WorkspaceInvitationGrpcService>();
app.MapGrpcService<WarpTalk.WorkspaceService.API.GrpcServices.WorkspaceGrpcService>();

// Simple health/check endpoints
app.MapGet("/", () => "WarpTalk Workspace Service is running.");

app.Run();

public partial class Program { }
