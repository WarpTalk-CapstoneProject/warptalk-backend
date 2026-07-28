using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Context;
using WarpTalk.BillingService.API.GrpcServices;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Application.Services.PaymentEventHandlers;
using WarpTalk.BillingService.Infrastructure.Extensions;
using WarpTalk.BillingService.Infrastructure.Services;
using WarpTalk.BillingService.Infrastructure.Workers;
using WarpTalk.BillingService.API.Extensions;
using WarpTalk.BillingService.API.Services;
using WarpTalk.BillingService.Domain.Constants;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "BillingService")
    .CreateLogger();

try
{
    Log.Information("Starting WarpTalk Billing Service...");

    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    if (!builder.Environment.IsDevelopment())
    {
        var billingDb = builder.Configuration.GetConnectionString("BillingDb") ?? string.Empty;
        var stripeSecretKey = builder.Configuration["Stripe:SecretKey"] ?? string.Empty;
        var jwtSecret = builder.Configuration["Jwt:Secret"] ?? string.Empty;

        if (billingDb.Contains(BillingMessageConstants.ConfigurationSecurity.LocalPostgresPasswordToken, StringComparison.OrdinalIgnoreCase) ||
            stripeSecretKey.Contains(BillingMessageConstants.ConfigurationSecurity.PlaceholderToken, StringComparison.OrdinalIgnoreCase) ||
            jwtSecret.Contains(BillingMessageConstants.ConfigurationSecurity.ChangeMeToken, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(BillingMessageConstants.ConfigurationSecurity.ProductionPlaceholderSecrets);
        }
    }

    builder.WebHost.ConfigureKestrel(options =>
    {
        // HTTP 1.1 for Swagger/REST
        options.ListenAnyIP(5107, listenOptions => listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1);

        // HTTP/2 for gRPC
        options.ListenAnyIP(50057, listenOptions => listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
    });

    builder.Services.AddBillingPersistence(builder.Configuration);

    // --- Application Services ---
    builder.Services.AddScoped<ICreditService, CreditService>();
    builder.Services.AddScoped<ICreditGrantService, CreditGrantService>();
    builder.Services.AddScoped<IPlanService, PlanService>();
    builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
    builder.Services.AddScoped<IPaymentService, PaymentService>();
    builder.Services.AddScoped<IPaymentEventHandler, CreditTopUpPaymentEventHandler>();
    builder.Services.AddScoped<IPaymentEventHandler, SubscriptionPaymentEventHandler>();
    builder.Services.AddScoped<IPaymentEventHandler, CancellationPaymentEventHandler>();
    builder.Services.AddScoped<IInvoiceService, InvoiceService>();
    builder.Services.AddScoped<IRefundService, RefundService>();
    builder.Services.AddScoped<IUsageService, UsageService>();
    builder.Services.AddScoped<IBillingAnalyticsService, BillingAnalyticsService>();
    builder.Services.AddScoped<IWorkspaceAuthorizationService, WorkspaceAuthorizationService>();
    builder.Services.AddScoped<IBillingRateService, BillingRateService>();
    builder.Services.AddScoped<IIdempotencyService, PersistentIdempotencyService>();
    builder.Services.AddScoped<IPaymentAppService, PaymentAppService>();
    builder.Services.AddScoped<IUsageSettlementService, PostgresUsageSettlementService>();

    // --- Infrastructure Services ---
    builder.Services.AddScoped<IStripePaymentService, StripePaymentService>();
    builder.Services.AddScoped<IStripeWebhookService, StripeWebhookService>();
    builder.Services.AddScoped<Stripe.SubscriptionService>();
    builder.Services.AddScoped<INotificationClient, WarpTalk.BillingService.Infrastructure.Clients.NotificationClient>();
    builder.Services.AddScoped<IWorkspaceClient, WarpTalk.BillingService.Infrastructure.Clients.WorkspaceClient>();

    Stripe.StripeConfiguration.ApiKey = builder.Configuration["Stripe:SecretKey"] ?? "sk_test_placeholder";

    // --- Grpc Clients ---
    builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient>(o =>
    {
        var url = builder.Configuration["NotificationServiceGrpcUrl"] ?? "http://localhost:50053";
        o.Address = new Uri(url);
    });

    builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.WorkspaceService.WorkspaceServiceClient>(o =>
    {
        var url = builder.Configuration["GrpcSettings:WorkspaceServiceUrl"] ?? "http://localhost:50056";
        o.Address = new Uri(url);
    });

    builder.Services.AddGrpc(options =>
    {
        options.EnableDetailedErrors = true;
    });
    builder.Services.AddGrpcReflection();

    builder.Services.AddSignalR();

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            var jwtSecret = builder.Configuration["Jwt:Secret"] ?? "CHANGE_ME_SUPER_SECRET_KEY_MIN_32_CHARS_LONG!!";
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = builder.Configuration["Jwt:Issuer"],
                ValidAudience = builder.Configuration["Jwt:Audience"],
                IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(jwtSecret)),
                ClockSkew = TimeSpan.FromSeconds(30)
            };

            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var accessToken = context.Request.Query["access_token"];
                    var path = context.HttpContext.Request.Path;
                    if (!string.IsNullOrEmpty(accessToken) &&
                        (path.StartsWithSegments("/hubs") ||
                         path.Value?.Contains("hub", StringComparison.OrdinalIgnoreCase) == true))
                    {
                        context.Token = accessToken;
                    }
                    return Task.CompletedTask;
                }
            };
        });

    builder.Services.AddAuthorization();

    builder.Services.AddHealthChecks()
        .AddNpgSql(
            builder.Configuration.GetConnectionString("BillingDb") ?? "",
            name: "Billing DB",
            tags: new[] { "db", "ready" });

    // --- Background Workers ---
    builder.Services.AddHostedService<SubscriptionExpirationWorker>();
    builder.Services.AddHostedService<SessionMonitorWorker>();
    builder.Services.AddHostedService<BillingCycleWorker>();
    builder.Services.AddHostedService<InvoiceOverdueSweeper>();
    builder.Services.AddHostedService<DailyAuditAggregationWorker>();
    builder.Services.AddHostedService<BillingAggregationWorker>();
    builder.Services.AddHostedService<BillingRedisSubscriberService>();

    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        });
    
    builder.Services.AddOpenApi();

    var app = builder.Build();

    if (!app.Environment.IsDevelopment())
    {
        app.UseHsts();
        app.UseHttpsRedirection();
    }

    app.MapHealthChecks("/health");
    app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = r => r.Tags.Contains("live")
    });
    app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = r => r.Tags.Contains("ready")
    });

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();
    app.MapGrpcService<BillingServiceGrpc>();
    app.MapHub<WarpTalk.BillingService.API.Hubs.BillingHub>(BillingMessageConstants.Notifications.HubPaths.Billing);
    
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapGrpcReflectionService();
    }

    app.Services.VerifyBillingDatabase();
    Log.Information("Database connection verified");

    Log.Information("WarpTalk Billing Service started successfully on http://localhost:5107");
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "WarpTalk Billing Service terminated unexpectedly");
    Environment.Exit(1);
}
finally
{
    await Log.CloseAndFlushAsync();
}
