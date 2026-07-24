using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Messaging;
using WarpTalk.BillingService.Infrastructure.Options;
using WarpTalk.BillingService.Infrastructure.Persistence;
using WarpTalk.BillingService.Infrastructure.Redis;
using WarpTalk.BillingService.Infrastructure.Repositories;

namespace WarpTalk.BillingService.Infrastructure.Extensions;

public static class BillingInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddBillingPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<BillingWorkerOptions>()
            .Bind(configuration.GetSection(BillingWorkerOptions.SectionName))
            .Validate(options =>
                options.SessionMonitorIntervalSeconds > 0 &&
                options.StaleReservationIntervalMinutes > 0 &&
                options.SubscriptionExpirationIntervalMinutes > 0 &&
                options.SubscriptionRenewalIntervalMinutes > 0 &&
                options.SubscriptionRenewalWindowHours > 0 &&
                options.SubscriptionRenewalLookbackHours > 0 &&
                options.DailyAuditHourUtc is >= 0 and <= 23 &&
                options.BillingAggregationIntervalMinutes > 0 &&
                options.BillingAggregationBatchSize > 0,
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
            ?? configuration["Redis:ConnectionString"]
            ?? "localhost:6379";

        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(redisConnectionString + ",abortConnect=false"));

        services.AddScoped<IRedisBillingStore, RedisBillingStore>();
        services.AddScoped<IBillingMessagePublisher, RedisBillingMessagePublisher>();

        return services;
    }

    public static void VerifyBillingDatabase(this IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<BillingDbContext>();
    }
}
