using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace WarpTalk.Shared.Extensions;

public static class ServiceHealthCheckExtensions
{
    public static IServiceCollection AddWarpTalkServiceHealthChecks<TDbContext>(
        this IServiceCollection services,
        string databaseCheckName)
        where TDbContext : DbContext
    {
        services
            .AddHealthChecks()
            .AddCheck(
                "self",
                () => HealthCheckResult.Healthy(),
                tags: ["live"])
            .AddCheck<DbContextConnectivityHealthCheck<TDbContext>>(
                databaseCheckName,
                tags: ["ready", "db"]);

        return services;
    }

    public static IEndpointRouteBuilder MapWarpTalkServiceHealthChecks(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health");
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("live")
        });
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("ready")
        });

        return endpoints;
    }

    public static IHealthChecksBuilder AddWarpTalkRedisReadiness(
        this IHealthChecksBuilder healthChecks,
        string checkName) =>
        healthChecks.AddCheck<RedisConnectivityHealthCheck>(
            checkName,
            tags: ["ready", "redis"]);
}

public sealed class DbContextConnectivityHealthCheck<TDbContext>(
    IServiceScopeFactory scopeFactory) : IHealthCheck
    where TDbContext : DbContext
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
            var canConnect = await dbContext.Database.CanConnectAsync(cancellationToken);

            return canConnect
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Database connection failed.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                "Database connectivity check failed.",
                exception);
        }
    }
}

public sealed class RedisConnectivityHealthCheck(
    IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!redis.IsConnected)
            {
                return HealthCheckResult.Unhealthy("Redis is disconnected.");
            }

            await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                "Redis connectivity check failed.",
                exception);
        }
    }
}
