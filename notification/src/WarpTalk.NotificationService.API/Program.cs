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
using WarpTalk.Shared.AdminAudit;
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


builder.Services.AddDbContext<NotificationDbContext>((provider, options) =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
        // [AdminAudited] announcement sends record their row before it commits.
        .AddAdminAuditInterceptor(provider));

// Announcements go into the platform audit log, hosted by the workspace service — the same
// address the audience resolver below uses (production and compose supply it). Without it the
// attribute stays inert and an announcement is recorded only in admin_notifications, as before:
// a missing address must not take every other endpoint down with it.
var adminAuditUrl = builder.Configuration["GrpcUrls:WorkspaceServiceUrl"];
if (!string.IsNullOrWhiteSpace(adminAuditUrl))
{
    builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.AdminAuditService.AdminAuditServiceClient>(o => o.Address = new Uri(adminAuditUrl))
        .AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);
    builder.Services.AddWarpTalkAdminAuditing(WarpTalk.Shared.Events.AdminAuditSources.NotificationService);
}
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
builder.Services.AddScoped<IAdminNotificationDeliveryService, AdminNotificationDeliveryService>();

// The email template CMS. This service owns the store; its own senders (and the GetEmailTemplate
// RPC every other sender calls) read it directly through DbEmailTemplateSource.
builder.Services.AddScoped<WarpTalk.Shared.Email.IEmailTemplateSource, DbEmailTemplateSource>();
builder.Services.AddScoped<WarpTalk.Shared.Email.IEmailTemplateComposer, WarpTalk.Shared.Email.EmailTemplateComposer>();
builder.Services.AddScoped<IAdminEmailTemplateService, AdminEmailTemplateService>();
builder.Services.AddScoped<IAnnouncementService, AnnouncementService>();

// WT-699 / TC4104: BROADCAST and SEGMENT announcements resolve their audience through AuthService
// and WorkspaceService. Optional configuration on purpose — without the two addresses those modes
// are refused with a sentence saying so, and SPECIFIC_USERS keeps working exactly as before.
var authServiceUrl = builder.Configuration["GrpcUrls:AuthServiceUrl"];
var workspaceServiceUrl = builder.Configuration["GrpcUrls:WorkspaceServiceUrl"];
if (!string.IsNullOrWhiteSpace(workspaceServiceUrl))
{
    builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.WorkspaceService.WorkspaceServiceClient>(o => o.Address = new Uri(workspaceServiceUrl))
        .AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);
    // Plan- and workspace-targeted announcements ask the workspace service who the viewer is.
    builder.Services.AddScoped<IViewerAudienceResolver, WarpTalk.NotificationService.API.Audience.GrpcViewerAudienceResolver>();
}
else
{
    builder.Services.AddSingleton<IViewerAudienceResolver, WarpTalk.NotificationService.API.Audience.UnconfiguredViewerAudienceResolver>();
}
if (!string.IsNullOrWhiteSpace(authServiceUrl) && !string.IsNullOrWhiteSpace(workspaceServiceUrl))
{
    builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.UserService.UserServiceClient>(o => o.Address = new Uri(authServiceUrl))
        .AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);
    builder.Services.AddScoped<IAdminAudienceResolver, WarpTalk.NotificationService.API.Audience.GrpcAdminAudienceResolver>();
}
else
{
    builder.Services.AddSingleton<IAdminAudienceResolver, WarpTalk.NotificationService.API.Audience.UnconfiguredAdminAudienceResolver>();
}
builder.Services.AddValidatorsFromAssemblyContaining<CreateAdminNotificationValidator>();

builder.Services.AddWarpTalkJwtAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddAuthorization();
builder.Services.AddWarpTalkStaffAuthorization(builder.Configuration, builder.Environment);
builder.Services.AddWarpTalkGrpcServer(builder.Configuration, builder.Environment);

// abortConnect=false: the notification read APIs are served from Postgres and must keep
// answering when Redis is down. Delivery is genuinely degraded in that state, which is why
// /health/ready carries the Redis check — refusing to boot would not have delivered anything
// either, and would have taken the read side down with it.
builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
    StackExchange.Redis.ConnectionMultiplexer.Connect(
        (builder.Configuration["Redis:ConnectionString"]
        ?? throw new InvalidOperationException("Redis:ConnectionString is not configured."))
        + ",abortConnect=false"));

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
