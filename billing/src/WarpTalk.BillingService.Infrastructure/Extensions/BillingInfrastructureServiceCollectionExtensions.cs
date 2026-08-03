using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Options;
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

        services.AddDbContext<BillingDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("BillingDb"),
                npgsqlOptions =>
                {
                    npgsqlOptions.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null);
                    npgsqlOptions.CommandTimeout(30);
                }));

        services.AddScoped<IUnitOfWork, UnitOfWork>();

        var redisConnectionString = configuration.GetConnectionString("Redis")
            ?? configuration["Redis:ConnectionString"];

        if (string.IsNullOrWhiteSpace(redisConnectionString))
        {
            throw new InvalidOperationException("Billing Redis connection string is not configured.");
        }

        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(redisConnectionString + ",abortConnect=false"));

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
        services.AddScoped<IUsageRateCardRepository, UsageRateCardRepository>();
        services.AddScoped<IUsageRateCardAdminService, UsageRateCardAdminService>();
        services.AddScoped<IUsageSettlementRepository, UsageSettlementRepository>();
        services.AddScoped<IStripeSdkClient, StripeSdkClient>();
        services.AddScoped<IOutboxClaimStore, OutboxClaimStore>();

        return services;
    }

    public static void VerifyBillingDatabase(this IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<BillingDbContext>();
    }
}
