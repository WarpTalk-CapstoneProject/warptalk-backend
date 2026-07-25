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
using WarpTalk.WorkspaceService.Infrastructure;
using WarpTalk.Shared.Extensions;
using MassTransit;

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
builder.Services.AddSingleton<IConnectionMultiplexer>(sp => ConnectionMultiplexer.Connect(redisConnectionString + ",abortConnect=false"));
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
builder.Services.AddScoped<IWorkspaceInvitationEmailComposer, WorkspaceInvitationEmailComposer>();
builder.Services.AddResendClient(builder.Configuration);
builder.Services.AddScoped<IWorkspaceDocumentService, WorkspaceDocumentService>();
builder.Services.AddScoped<IVerifiedDomainService, VerifiedDomainService>();
builder.Services.AddScoped<IDocumentTextExtractor, DocumentTextExtractor>();
builder.Services.AddScoped<IDocumentSecurityScanner, DocumentSecurityScanner>();
builder.Services.AddScoped<IDocumentAccessEvaluator, DocumentAccessEvaluator>();
builder.Services.AddScoped<IWorkspaceDocumentEventPublisher, HybridWorkspaceDocumentEventPublisher>();
builder.Services.AddScoped<IWorkspaceEventPublisher, RedisWorkspaceEventPublisher>();
builder.Services.AddInfrastructureServices(builder.Configuration);
builder.Services.AddHttpClient();
builder.Services.AddHostedService<DocumentSecurityGuardrailConsumerService>();
builder.Services.AddHostedService<DocumentEmbeddingIndexResultConsumerService>();
builder.Services.AddHostedService<MeetingStartedEventConsumer>();

builder.Services.AddWarpTalkMassTransit(builder.Configuration);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IWorkspaceUrlProvider, WorkspaceUrlProvider>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var secretKey = builder.Configuration["Jwt:Secret"]
            ?? builder.Configuration["JwtSettings:SecretKey"]
            ?? "super_secret_jwt_key_that_is_at_least_32_bytes_long_123456";

        var issuer = builder.Configuration["Jwt:Issuer"]
            ?? builder.Configuration["JwtSettings:Issuer"]
            ?? "WarpTalk.AuthService";

        var audience = builder.Configuration["Jwt:Audience"]
            ?? builder.Configuration["JwtSettings:Audience"]
            ?? "WarpTalk";

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = issuer,
            ValidAudience = audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey))
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddGrpc();
builder.Services.AddControllers();

builder.Services.AddEndpointsApiExplorer();

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
