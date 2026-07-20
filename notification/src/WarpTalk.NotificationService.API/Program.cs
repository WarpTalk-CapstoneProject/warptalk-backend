using Microsoft.EntityFrameworkCore;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Infrastructure.Persistence;
using WarpTalk.NotificationService.Infrastructure.Repositories;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Application.Services;
using WarpTalk.NotificationService.API.GrpcServices;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Text.Json.Serialization;
using FluentValidation;
using WarpTalk.NotificationService.API.Validators;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    // HTTP/1-only port for REST API Gateway
    options.ListenAnyIP(5104, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1;
    });

    // HTTP/2-only port for gRPC
    options.ListenAnyIP(50054, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
    });
});

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Enforce FR-002: Reject unknown top-level fields
        options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    });


builder.Services.AddDbContext<NotificationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IAdminNotificationService, AdminNotificationService>();
builder.Services.AddValidatorsFromAssemblyContaining<CreateAdminNotificationValidator>();

var rawJwtSecret = builder.Configuration["Jwt:Secret"];
var isDefaultOrInvalid = string.IsNullOrWhiteSpace(rawJwtSecret) || 
                         rawJwtSecret.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase) ||
                         rawJwtSecret.Length < 32;

// [Security] Prevent starting in Production with a weak or default JWT secret to avoid token forgery.
if (builder.Environment.IsProduction() && isDefaultOrInvalid)
{
    throw new InvalidOperationException("CRITICAL SECURITY: JWT Secret is not properly configured for Production. It must be at least 32 characters long and not be the default placeholder.");
}

// In non-production, fallback to default if invalid
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
// [Security] Register global interceptor to enforce Zero-Trust Authentication for all incoming gRPC calls.
builder.Services.AddGrpc(options => 
{
    options.Interceptors.Add<WarpTalk.NotificationService.API.Interceptors.InternalAuthInterceptor>();
});

builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
    StackExchange.Redis.ConnectionMultiplexer.Connect(builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379"));

builder.Services.AddSingleton<WarpTalk.NotificationService.Domain.Interfaces.IMessagePublisher, WarpTalk.NotificationService.Infrastructure.Messaging.RedisMessagePublisher>();

// Register Downstream Worker for Admin Notifications
builder.Services.AddHostedService<WarpTalk.NotificationService.API.HostedServices.NotificationStreamConsumerService>();

var app = builder.Build();



app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGrpcService<NotificationGrpcServiceImpl>();

app.Run();

// Make Program available for integration tests
public partial class Program { }
