using System;
using Microsoft.EntityFrameworkCore;
using WarpTalk.Shared.AdminAudit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Options;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Messaging;
using WarpTalk.BillingService.Infrastructure.Options;
using WarpTalk.BillingService.Infrastructure.Persistence;
using WarpTalk.BillingService.Infrastructure.Redis;
using WarpTalk.BillingService.Infrastructure.Repositories;
using WarpTalk.BillingService.Infrastructure.Services;

namespace WarpTalk.BillingService.Infrastructure.Extensions;

public static class BillingInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddBillingPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<BillingPolicyOptions>()
            .Bind(configuration.GetSection(BillingPolicyOptions.SectionName))
            .Validate(options =>
                options.VatRate is >= 0 and <= 1,
                "Billing policy options are missing or invalid.")
            .ValidateOnStart();

        services
            .AddOptions<BillingWorkerOptions>()
            .Bind(configuration.GetSection(BillingWorkerOptions.SectionName))
            .Validate(options =>
                options.SessionMonitorIntervalSeconds > 0 &&
                options.SubscriptionExpirationIntervalMinutes > 0 &&
                options.SubscriptionRenewalLookbackHours > 0 &&
                options.DailyAuditHourUtc is >= 0 and <= 23 &&
                options.BillingAggregationIntervalSeconds > 0 &&
                options.BillingAggregationBatchSize > 0 &&
                options.BillingCycleIntervalMinutes > 0 &&
                options.InvoiceOverdueIntervalMinutes > 0,
                "Billing worker options are missing or invalid.")
            .ValidateOnStart();

        services.AddDbContext<BillingDbContext>((provider, options) =>
            options.UseNpgsql(
                configuration.GetConnectionString("BillingDb"),
                npgsqlOptions =>
                {
                    npgsqlOptions.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null);
                    npgsqlOptions.CommandTimeout(30);
                })
            // [AdminAudited] routes record each save before it commits (see AddWarpTalkAdminAuditing).
            .AddAdminAuditInterceptor(provider));

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped(sp => sp.GetRequiredService<IUnitOfWork>().SubscriptionRepository);

        var redisConnectionString = configuration.GetConnectionString("Redis")
            ?? configuration["Redis:ConnectionString"];

        if (string.IsNullOrWhiteSpace(redisConnectionString))
        {
            throw new InvalidOperationException("Billing Redis connection string is not configured.");
        }

        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(redisConnectionString + ",abortConnect=false"));

        // Platform settings (/admin/settings): trial length and credits are read live.
        WarpTalk.Shared.PlatformSettings.PlatformSettingsServiceCollectionExtensions.AddWarpTalkPlatformSettings(services);
        WarpTalk.Shared.PlatformSettings.IntegrationStatusServiceCollectionExtensions.AddWarpTalkIntegrationStatus(services, "billing", sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            return WarpTalk.Shared.PlatformSettings.IntegrationStatusServiceCollectionExtensions.Snapshot(
                (WarpTalk.Shared.PlatformSettings.IntegrationKeys.Stripe,
                    WarpTalk.Shared.PlatformSettings.IntegrationReport.FromConfiguration(config, "checkout and webhooks", "Stripe:SecretKey", "Stripe:WebhookSecret")),
                (WarpTalk.Shared.PlatformSettings.IntegrationKeys.Cartesia,
                    WarpTalk.Shared.PlatformSettings.IntegrationReport.FromConfiguration(config, "usage sync", "Cartesia:AdminApiKey")),
                (WarpTalk.Shared.PlatformSettings.IntegrationKeys.ObjectStorage,
                    WarpTalk.Shared.PlatformSettings.IntegrationReport.ObjectStorage(config, "expense receipts")));
        });

        services.AddScoped<RedisBillingStore>();
        services.AddScoped<IRedisBillingStore>(sp => sp.GetRequiredService<RedisBillingStore>());
        services.AddScoped<IBillingUsageQueue>(sp => sp.GetRequiredService<RedisBillingStore>());
        services.AddScoped<IAiServiceStateStore>(sp => sp.GetRequiredService<RedisBillingStore>());
        services.AddScoped<ISessionActivityStore>(sp => sp.GetRequiredService<RedisBillingStore>());
        services.AddScoped<IBillingMessagePublisher, RedisBillingMessagePublisher>();
        services.AddScoped<IBillingCycleClosingService, BillingCycleClosingService>();
        services.AddScoped<IBillingOperationalAlertService, BillingOperationalAlertService>();
        services.AddScoped<IBillingPolicyRepository, BillingPolicyRepository>();
        services.AddScoped<IBillingPolicyService, BillingPolicyService>();
        services.AddMemoryCache();
        services.AddScoped<IUsageRateCardRepository, UsageRateCardRepository>();
        services.AddScoped<IUsageRateCardAdminService, UsageRateCardAdminService>();
        services.AddScoped<IUsageRateCardResolverService, UsageRateCardResolverService>();
        services.AddScoped<IUsageSettlementRepository, UsageSettlementRepository>();
        services.AddScoped<IStripeSdkClient, StripeSdkClient>();
        // G11: pushes credit packs, add-ons and coupons to Stripe Products/Prices/Coupons (admin only).
        services.AddScoped<IStripeCatalogSync, StripeCatalogSyncService>();
        // #466: recurring plan billing (auto-renew = a Stripe Subscription).
        services.AddScoped<IStripeRecurringGateway, StripeRecurringGateway>();
        // Stripe's USD→VND rate, recorded daily (FxRateRefreshWorker) and read by every VND report.
        // Every Stripe call is counted for the admin Providers page (StripeCallObserver); the FX
        // client builds its own StripeClient, so it is handed the observed HTTP client too.
        services.AddSingleton<IProviderCallRecorder, RedisProviderCallRecorder>();
        services.AddSingleton<IStripeFxClient>(sp => new StripeFxClient(
            configuration["Stripe:SecretKey"],
            configuration["Stripe:FxQuotesApiVersion"],
            StripeCallObserver.CreateStripeHttpClient(sp.GetRequiredService<IProviderCallRecorder>())));
        services.AddScoped<IFxRateService, WarpTalk.BillingService.Application.Services.FxRateService>();
        services.AddScoped<IOutboxClaimStore, OutboxClaimStore>();

        AddCartesiaUsageSync(services, configuration);
        AddProviderStatus(services, configuration);

        // WT-263: the entitlement layer. The resolver is the single place entitlements are computed;
        // the publisher is the single place they leave this service.
        services.AddScoped<
            WarpTalk.BillingService.Application.Entitlements.IEntitlementResolver,
            WarpTalk.BillingService.Application.Entitlements.EntitlementResolver>();
        services.AddScoped<
            WarpTalk.BillingService.Application.Entitlements.IEntitlementChangePublisher,
            WarpTalk.BillingService.Application.Entitlements.EntitlementChangePublisher>();

        return services;
    }

    /// <summary>
    /// Cartesia usage sync: options, the HTTP client, and the status the Insights snapshot reads.
    /// Registered whether or not an admin key is configured — without one the worker only marks
    /// itself disabled, and Insights fall back to rate-card estimates.
    /// </summary>
    private static void AddCartesiaUsageSync(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(CartesiaUsageOptions.SectionName);
        services.AddOptions<CartesiaUsageOptions>().Bind(section);

        var options = section.Get<CartesiaUsageOptions>() ?? new CartesiaUsageOptions();
        services.AddHttpClient(CartesiaUsageOptions.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 5, 120));
        });
        services.AddSingleton<ICartesiaUsageClient, CartesiaUsageClient>();

        // Shared through Redis: billing runs several replicas, only one syncs at a time, and every
        // replica's Insights snapshot has to report the sync that actually ran.
        services.AddSingleton<ICartesiaSyncCoordinator>(sp =>
            new RedisCartesiaSyncCoordinator(sp.GetRequiredService<IConnectionMultiplexer>()));
        services.AddSingleton<ICartesiaUsageSyncStatus>(sp =>
            new RedisCartesiaUsageSyncStatus(
                sp.GetRequiredService<IConnectionMultiplexer>(),
                configured: options.IsConfigured && options.UsageSyncIntervalMinutes > 0,
                filteredToApiKey: options.IsFilteredToApiKey,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RedisCartesiaUsageSyncStatus>>()));
    }

    /// <summary>
    /// Admin Providers page: status page polling options, its HTTP client and shared state. The
    /// pages are public URLs (ProviderStatus:Pages:{provider}); none configured = no polling.
    /// </summary>
    private static void AddProviderStatus(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ProviderStatusOptions>().Bind(configuration.GetSection(ProviderStatusOptions.SectionName));
        services.AddHttpClient(Workers.ProviderStatusPollWorker.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WarpTalk-Billing-StatusPoll/1.0");
        });
        services.AddSingleton<IProviderStatusPageState>(sp =>
            new RedisProviderStatusPageState(sp.GetRequiredService<IConnectionMultiplexer>()));
    }

    public static void VerifyBillingDatabase(this IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<BillingDbContext>();
    }
}
