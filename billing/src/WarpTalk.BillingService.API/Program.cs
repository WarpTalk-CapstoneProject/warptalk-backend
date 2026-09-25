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

using WarpTalk.BillingService.API.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Messaging;
using WarpTalk.BillingService.Infrastructure.Redis;
using WarpTalk.BillingService.Infrastructure.Repositories;
using WarpTalk.BillingService.Infrastructure.Clients;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Coordination;
using WarpTalk.Shared.Extensions;
using WarpTalk.Shared.AdminAudit;
using WarpTalk.Shared.Grpc;

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
    builder.Services.AddWarpTalkObservability(
        builder.Configuration,
        builder.Environment,
        "warptalk-billing");

    if (!builder.Environment.IsDevelopment())
    {
        var billingDb = builder.Configuration.GetConnectionString("BillingDb") ?? string.Empty;
        var stripeSecretKey = builder.Configuration["Stripe:SecretKey"] ?? string.Empty;
        var jwtSecret = builder.Configuration["Jwt:Secret"] ?? string.Empty;

        if (string.IsNullOrWhiteSpace(billingDb) ||
            string.IsNullOrWhiteSpace(stripeSecretKey) ||
            string.IsNullOrWhiteSpace(jwtSecret) ||
            billingDb.Contains(BillingMessageConstants.ConfigurationSecurity.LocalPostgresPasswordToken, StringComparison.OrdinalIgnoreCase) ||
            stripeSecretKey.Contains(BillingMessageConstants.ConfigurationSecurity.PlaceholderToken, StringComparison.OrdinalIgnoreCase) ||
            jwtSecret.Contains(BillingMessageConstants.ConfigurationSecurity.ChangeMeToken, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(BillingMessageConstants.ConfigurationSecurity.ProductionPlaceholderSecrets);
        }
    }

    builder.WebHost.ConfigureKestrel(options =>
    {
        var httpPort = builder.Configuration.GetValue<int?>("Billing:HttpPort") ?? 5107;
        var grpcPort = builder.Configuration.GetValue<int?>("Billing:GrpcPort") ?? 50057;

        // HTTP 1.1 for Swagger/REST
        options.ListenAnyIP(httpPort, listenOptions => listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1);

        // HTTP/2 for gRPC
        options.ListenAnyIP(grpcPort, listenOptions => listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
    });

    builder.Services.AddBillingPersistence(builder.Configuration);

    // --- Application Services ---
    builder.Services.AddScoped<ICreditService, CreditService>();
builder.Services.AddScoped<IAdminSubscriptionService, AdminSubscriptionService>();

    builder.Services.AddScoped<IPlanService, PlanService>();
    builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
    builder.Services.AddScoped<IPaymentService, PaymentService>();

    // G11 — FIRST, deliberately: PaymentAppService takes the first handler that claims an event,
    // and CancellationPaymentEventHandler claims every Cancelled/Refunded one. An add-on's Stripe
    // subscription ending, or a credit pack refunded, must reach these and never cancel the plan.
    builder.Services.AddScoped<IPaymentEventHandler, CreditPackPaymentEventHandler>();
    builder.Services.AddScoped<IPaymentEventHandler, AddOnPaymentEventHandler>();
    builder.Services.AddScoped<IPaymentEventHandler, SubscriptionPaymentEventHandler>();
    builder.Services.AddScoped<IPaymentEventHandler, CancellationPaymentEventHandler>();
    // WT-429: without this, "CreditTopUp" matched no handler and the payment completed having
    // granted nothing — the incident that switched the top-up button off.
    builder.Services.AddScoped<IPaymentEventHandler, CreditTopUpPaymentEventHandler>();
    builder.Services.AddScoped<IInvoiceService, InvoiceService>();

    builder.Services.AddScoped<IUsageService, UsageService>();
    builder.Services.AddScoped<IBillingAnalyticsService, BillingAnalyticsService>();
    builder.Services.AddScoped<IWorkspaceAuthorizationService, WorkspaceAuthorizationService>();
    builder.Services.AddScoped<IPaymentAppService, PaymentAppService>();
    builder.Services.AddScoped<WarpTalk.BillingService.Domain.Services.ISubscriptionDomainService, WarpTalk.BillingService.Domain.Services.SubscriptionDomainService>();
    builder.Services.AddScoped<IUsageSettlementService, WarpTalk.BillingService.Infrastructure.Services.PostgresUsageSettlementService>();
    builder.Services.AddScoped<ISalesInquiryService, SalesInquiryService>();
    // G12 internal management: operating expenses (receipts in object storage) and billing's inbox sources.
    builder.Services.AddScoped<IOperatingExpenseService, WarpTalk.BillingService.Application.Services.Expenses.OperatingExpenseService>();
    builder.Services.AddScoped<IAdminInboxSourceService, AdminInboxSourceService>();
    WarpTalk.BillingService.Infrastructure.Storage.ExpenseReceiptStorageServiceCollectionExtensions.AddExpenseReceiptStorage(
        builder.Services, builder.Configuration, builder.Environment);
    // G11 — the sellable catalog beyond plans: /admin/packages and the workspace billing page.
    builder.Services.AddScoped<IPackageCatalogService, PackageCatalogService>();
    builder.Services.AddScoped<ICustomerCatalogService, CustomerCatalogService>();
    builder.Services.AddScoped<ICreditPackExpiryService, CreditPackExpiryService>();

    // --- Infrastructure Services ---
    builder.Services.AddScoped<IStripePaymentService, StripePaymentService>();
    builder.Services.AddScoped<IStripeWebhookService, StripeWebhookService>();
    builder.Services.AddScoped<Stripe.SubscriptionService>();
    builder.Services.AddScoped<INotificationClient, WarpTalk.BillingService.Infrastructure.Clients.NotificationClient>();
    builder.Services.AddScoped<IWorkspaceClient, WarpTalk.BillingService.Infrastructure.Clients.WorkspaceClient>();

    Stripe.StripeConfiguration.ApiKey = builder.Configuration["Stripe:SecretKey"] ?? string.Empty;

    // --- Grpc Clients ---
    builder.Services.AddScoped<IAdminWorkspaceAnalyticsService, AdminWorkspaceAnalyticsService>();
    builder.Services.AddScoped<IAdminBillingInsightsService, AdminBillingInsightsService>();
    // Admin Providers page. The option facts say whether a secret is SET, never what it is.
    builder.Services.AddSingleton(new AdminProvidersOptions
    {
        StatusPages = (builder.Configuration.GetSection(WarpTalk.BillingService.Infrastructure.Options.ProviderStatusOptions.SectionName)
                .Get<WarpTalk.BillingService.Infrastructure.Options.ProviderStatusOptions>()?.Pages
            ?? new Dictionary<string, string>())
            .Where(page => Uri.TryCreate(page.Value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            .ToDictionary(page => page.Key.ToLowerInvariant(), page => page.Value.TrimEnd('/'), StringComparer.OrdinalIgnoreCase),
        StripeSecretKeyConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["Stripe:SecretKey"]),
        StripeWebhookSecretConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["Stripe:WebhookSecret"]),
    });
    builder.Services.AddScoped<IMediaUsageClient, WarpTalk.BillingService.Infrastructure.Clients.MediaUsageGrpcClient>();
    builder.Services.AddScoped<IAdminProvidersService, AdminProvidersService>();
    builder.Services.AddScoped<IAdminWorkspaceBillingService, AdminWorkspaceBillingService>();
    builder.Services.AddScoped<IAdminAuditRecorder, WarpTalk.BillingService.Infrastructure.Clients.AdminAuditGrpcClient>();

    builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient>(o =>
    {
        var url = builder.Configuration["NotificationServiceGrpcUrl"] ?? "http://localhost:50054";
        o.Address = new Uri(url);
    })
    .AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);

    builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.WorkspaceService.WorkspaceServiceClient>(o =>
    {
        var url = builder.Configuration["GrpcSettings:WorkspaceServiceUrl"]
            ?? builder.Configuration["GrpcUrls:WorkspaceServiceUrl"]
            ?? "http://localhost:50056";
        o.Address = new Uri(url);
    })
    .AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);

    // LiveKit usage for the admin Providers page (translation-room GetMediaUsage).
    builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.TranslationRoomService.TranslationRoomServiceClient>(o =>
    {
        var url = builder.Configuration["GrpcSettings:TranslationRoomServiceUrl"]
            ?? builder.Configuration["GrpcUrls:TranslationRoomServiceUrl"]
            ?? "http://localhost:50052";
        o.Address = new Uri(url);
    })
    .AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);

    // The platform audit log lives in the workspace service: same address as the workspace client
    // above, a second contract on it. Synchronous so an admin action that cannot be recorded is
    // refused rather than committed unaudited (see IAdminAuditRecorder).
    builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.AdminAuditService.AdminAuditServiceClient>(o =>
    {
        var url = builder.Configuration["GrpcSettings:WorkspaceServiceUrl"]
            ?? builder.Configuration["GrpcUrls:WorkspaceServiceUrl"]
            ?? "http://localhost:50056";
        o.Address = new Uri(url);
    })
    .AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);

    // Platform writes that predate the admin workspace page — plans, rate cards, pricing, VAT,
    // contracts, the /admin/subscriptions buttons, the legacy mark-paid, sales leads — are audited
    // by [AdminAudited] on their routes: each save is recorded before it commits, over the client
    // above, and refused if it cannot be.
    builder.Services.AddWarpTalkAdminAuditing(WarpTalk.Shared.Events.AdminAuditSources.BillingService);

    builder.Services.AddWarpTalkGrpcServer(builder.Configuration, builder.Environment);
    builder.Services.Configure<Grpc.AspNetCore.Server.GrpcServiceOptions>(options =>
    {
        options.EnableDetailedErrors = true;
    });
    builder.Services.AddGrpcReflection();

    // Backplane so IHubContext<BillingHub> sends reach every replica's connections. Note the
    // ingress routes /hubs/* to the GATEWAY, whose own BillingHub serves /hubs/billing; this hub is
    // only reachable by calling the billing service directly.
    var signalR = builder.Services.AddSignalR();
    var backplaneRedis = SignalRBackplaneExtensions.ResolveBackplaneConnectionString(builder.Configuration);
    if (backplaneRedis is not null)
    {
        signalR.AddStackExchangeRedis(backplaneRedis, options =>
        {
            options.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("WarpTalk.Billing");
            options.Configuration.AbortOnConnectFail = false;
        });
    }

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            var jwtSecret = builder.Configuration["Jwt:Secret"] ?? string.Empty;
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
    // Multi-replica: the periodic workers take a Redis lease per tick (IDistributedLockProvider,
    // see each worker's LockResource), and one replica at a time relays pub/sub into BillingHub.
    // Registered before the workers so the elector starts first and stops (releasing) last.
    builder.Services.AddWarpTalkDistributedLocks();
    builder.Services.AddWarpTalkPubSubLeadership(
        BillingRedisSubscriberService.LeaseResource,
        [BillingRedisSubscriberService.SubscriptionKey]);
    builder.Services.AddHostedService<SubscriptionExpirationWorker>();
    builder.Services.AddHostedService<SessionMonitorWorker>();
    builder.Services.AddHostedService<BillingCycleWorker>();
    builder.Services.AddHostedService<InvoiceOverdueSweeper>();
    builder.Services.AddHostedService<DailyAuditAggregationWorker>();
    builder.Services.AddHostedService<BillingAggregationWorker>();
    builder.Services.AddHostedService<BillingOutboxWorker>();
    builder.Services.AddHostedService<BillingRedisSubscriberService>();
    // WT-430: republishes every workspace's entitlements on a slow interval, so a consumer's
    // snapshot cannot stay silently stale after a change that did not go through billing's own
    // write paths. Disabled by setting Billing:Workers:EntitlementReconcileIntervalMinutes to 0.
    builder.Services.AddHostedService<EntitlementReconcileWorker>();
    // Measured Cartesia credits for admin Insights' dubbing cost. Disabled (logged once) when no
    // Cartesia:AdminApiKey (CARTESIA_ADMIN_API_KEY) is configured.
    builder.Services.AddHostedService<CartesiaUsageSyncWorker>();
    // Stripe's USD→VND rate once per UTC day, for every VND report. Billing:Fx:CheckIntervalMinutes = 0 disables it.
    builder.Services.AddHostedService<FxRateRefreshWorker>();
    // G11: removes the unspent part of credit packs whose validity ended. Billing:CreditPacks:ExpiryIntervalMinutes = 0 disables it.
    builder.Services.AddHostedService<CreditPackExpiryWorker>();
    // Admin Providers page: our provider calls (Redis → Postgres) and the providers' public status pages.
    // ProviderStatus:CallStatsSyncIntervalMinutes / PollIntervalMinutes = 0 disable them; no pages = no polling.
    builder.Services.AddHostedService<ProviderCallStatsSyncWorker>();
    builder.Services.AddHostedService<ProviderStatusPollWorker>();
    // Stripe's fee per paid payment (balance transaction) — the Stripe cost on the Providers page.
    builder.Services.AddHostedService<StripeFeeSyncWorker>();
    // G12: writes each next occurrence of a recurring operating expense a week before it is due.
    builder.Services.AddHostedService<ExpenseRecurrenceWorker>();

    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        });

    // Staff permissions for every admin endpoint (G10, replacing the WT-205 system-admin gate):
    // [RequirePermission] asks the auth service who is staff, through a short cache.
    builder.Services.AddWarpTalkStaffAuthorization(builder.Configuration, builder.Environment);

    var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? new[] { "*" };
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowSpecificOrigins", policy =>
        {
            policy.WithOrigins(corsOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        });
    });

    builder.Services.AddOpenApi();

    var app = builder.Build();

    // Admin Providers page: one StripeClient for the whole service, over an HTTP handler that counts
    // every call by outcome and latency (StripeCallObserver). Services created with `new XService()`
    // read StripeConfiguration.StripeClient, so this covers all of them. No key, no client: the
    // SDK's own lazy client would refuse to call Stripe anyway.
    var observedStripeKey = builder.Configuration["Stripe:SecretKey"];
    if (!string.IsNullOrWhiteSpace(observedStripeKey))
    {
        Stripe.StripeConfiguration.StripeClient = new Stripe.StripeClient(
            apiKey: observedStripeKey,
            httpClient: StripeCallObserver.CreateStripeHttpClient(app.Services.GetRequiredService<IProviderCallRecorder>()));
    }

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
