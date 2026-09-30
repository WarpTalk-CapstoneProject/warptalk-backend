using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace WarpTalk.Shared.Coordination;

public static class CoordinationServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IDistributedLockProvider"/>. Uses Redis (<see cref="RedisLeaseStore"/>)
    /// whenever an <see cref="IConnectionMultiplexer"/> is registered — which every service that
    /// runs more than one replica must have — and otherwise an in-process store that only
    /// coordinates within one process, logged as a warning at first use.
    /// </summary>
    public static IServiceCollection AddWarpTalkDistributedLocks(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ILeaseStore>(sp =>
        {
            var redis = sp.GetService<IConnectionMultiplexer>();
            if (redis is not null)
            {
                return new RedisLeaseStore(redis);
            }

            sp.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(CoordinationServiceCollectionExtensions))
                .LogWarning(
                    "No Redis is configured, so distributed locks only coordinate within this process. "
                    + "That is fine for one replica and WRONG for more than one.");
            return new InProcessLeaseStore(sp.GetRequiredService<TimeProvider>());
        });
        services.TryAddSingleton<IDistributedLockProvider, DistributedLockProvider>();
        return services;
    }

    /// <summary>
    /// Registers a <see cref="LeaderElector"/> for <paramref name="resource"/> as both the
    /// process's <see cref="ILeaderElection"/> and a hosted service. An
    /// <see cref="ILeaderEligibility"/> registered in DI, if any, gates standing for election.
    /// </summary>
    public static IServiceCollection AddWarpTalkLeaderElection(
        this IServiceCollection services,
        string resource,
        Action<LeaderElectionOptions>? configure = null)
    {
        services.AddWarpTalkDistributedLocks();

        var options = new LeaderElectionOptions { Resource = resource };
        configure?.Invoke(options);

        services.TryAddSingleton(sp => new LeaderElector(
            sp.GetRequiredService<IDistributedLockProvider>(),
            options,
            sp.GetRequiredService<ILogger<LeaderElector>>(),
            sp.GetService<ILeaderEligibility>(),
            sp.GetService<IConnectionMultiplexer>()));
        services.TryAddSingleton<ILeaderElection>(sp => sp.GetRequiredService<LeaderElector>());
        services.AddHostedService(sp => sp.GetRequiredService<LeaderElector>());
        return services;
    }
}
