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
using WarpTalk.NotificationService.API.Consumers;
using WarpTalk.NotificationService.API.HostedServices;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Extensions;
using WarpTalk.Shared.Grpc;
using MassTransit;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWarpTalkObservability(
    builder.Configuration,
    builder.Environment,
    "warptalk-notification");

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
builder.Services.AddWarpTalkServiceHealthChecks<NotificationDbContext>(
    "notification-database");

builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
builder.Services.AddScoped<BillingNotificationEventHandler>();
builder.Services.AddScoped<RealtimeNotificationPersistenceHandler>();
builder.Services.AddHostedService<RealtimeNotificationPersistenceService>();
builder.Services.AddWarpTalkMassTransit(
    builder.Configuration,
    registration => registration.AddConsumer<
        BillingNotificationEventConsumer,
        BillingNotificationEventConsumerDefinition>());

// Register official Resend .NET SDK
builder.Services.AddOptions();
builder.Services.AddHttpClient<Resend.ResendClient>();
var resendApiToken = builder.Configuration["RESEND_API_KEY"]
                     ?? builder.Configuration["Resend:ApiKey"]
                     ?? Environment.GetEnvironmentVariable("RESEND_API_KEY");
if (builder.Environment.IsProduction() &&
    (string.IsNullOrWhiteSpace(resendApiToken)
     || resendApiToken.Contains("placeholder", StringComparison.OrdinalIgnoreCase)
     || resendApiToken.StartsWith("CHANGE_ME", StringComparison.OrdinalIgnoreCase)))
{
    throw new InvalidOperationException(
        "CRITICAL SECURITY ERROR: a non-placeholder Resend API key is required in Production.");
}
builder.Services.Configure<Resend.ResendClientOptions>(o =>
{
    o.ApiToken = resendApiToken ?? string.Empty;
});
builder.Services.AddTransient<Resend.IResend, Resend.ResendClient>();
builder.Services.AddTransient<IEmailSender, WarpTalk.NotificationService.Infrastructure.Services.ResendEmailSender>();

builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IAdminNotificationService, AdminNotificationService>();
builder.Services.AddValidatorsFromAssemblyContaining<CreateAdminNotificationValidator>();

builder.Services.AddWarpTalkJwtAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddAuthorization();
builder.Services.AddWarpTalkSystemAdminAuthorization();
builder.Services.AddWarpTalkGrpcServer(builder.Configuration, builder.Environment);

builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
    StackExchange.Redis.ConnectionMultiplexer.Connect(
        builder.Configuration["Redis:ConnectionString"]
        ?? throw new InvalidOperationException("Redis:ConnectionString is not configured.")));

builder.Services.AddSingleton<WarpTalk.NotificationService.Domain.Interfaces.IMessagePublisher, WarpTalk.NotificationService.Infrastructure.Messaging.RedisMessagePublisher>();

// Register Downstream Worker for Admin Notifications
builder.Services.AddHostedService<WarpTalk.NotificationService.API.HostedServices.NotificationStreamConsumerService>();

var app = builder.Build();



app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGrpcService<NotificationGrpcServiceImpl>();
app.MapWarpTalkServiceHealthChecks();

app.Run();

// Make Program available for integration tests
public partial class Program { }
