using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace WarpTalk.Shared.Coordination;

/// <summary>
/// "Only one replica acts on this Redis pub/sub message."
///
/// Pub/sub delivers every message to every subscriber, so a handler with side effects (a SignalR
/// broadcast that the backplane already fans out to every pod, a database read-modify-write)
/// runs once per replica. The fix used here keeps every replica subscribed — a hot standby that
/// needs no resubscribe on failover — and lets only the elected leader act.
///
/// Delivery guarantee: the same at-most-once as pub/sub itself, plus a bounded gap on failover —
/// messages published between a leader crash and the takeover (at most LeaseDuration +
/// AcquireRetryInterval, 6s by default) are dropped; on a graceful shutdown the gap is one Redis
/// round trip. Duplicates are prevented as long as no process pauses for longer than the lease
/// safety margin between <see cref="ShouldHandle"/> and the send. See <see cref="LeaderElector"/>.
/// </summary>
public interface IPubSubLeadership
{
    /// <summary>True only on the leader while its lease is valid. Check it per message.</summary>
    bool ShouldHandle { get; }

    /// <summary>Report that one of the required subscriptions is registered on this replica.</summary>
    void MarkSubscribed(string subscriptionKey);
}

/// <summary>
/// Stands a replica for election only once every required subscription is registered and the
/// pub/sub connection is up. A leader whose subscriber socket is down would keep the lease while
/// handling nothing, and every healthy follower would drop every message meanwhile.
/// </summary>
public sealed class PubSubSubscriptionEligibility : ILeaderEligibility
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IReadOnlyCollection<string> _required;
    private readonly ConcurrentDictionary<string, byte> _subscribed = new(StringComparer.Ordinal);

    public PubSubSubscriptionEligibility(IConnectionMultiplexer redis, IReadOnlyCollection<string> requiredSubscriptions)
    {
        _redis = redis;
        _required = requiredSubscriptions;
    }

    public IReadOnlyCollection<string> RequiredSubscriptions => _required;

    public void MarkSubscribed(string subscriptionKey) => _subscribed[subscriptionKey] = 0;

    public bool AllSubscribed => _required.All(_subscribed.ContainsKey);

    public bool IsEligible => AllSubscribed && _redis.GetSubscriber().IsConnected();
}

public sealed class PubSubLeadership : IPubSubLeadership
{
    private readonly ILeaderElection _election;
    private readonly PubSubSubscriptionEligibility _eligibility;

    public PubSubLeadership(ILeaderElection election, PubSubSubscriptionEligibility eligibility)
    {
        _election = election;
        _eligibility = eligibility;
    }

    public bool ShouldHandle => _election.IsLeader;

    public void MarkSubscribed(string subscriptionKey) => _eligibility.MarkSubscribed(subscriptionKey);
}

public static class PubSubLeadershipServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IPubSubLeadership"/> backed by a <see cref="LeaderElector"/> on
    /// <paramref name="resource"/>. Every key in <paramref name="requiredSubscriptions"/> must be
    /// reported through <see cref="IPubSubLeadership.MarkSubscribed"/> before this replica may
    /// lead. One per process: the elector is the process's <see cref="ILeaderElection"/>.
    /// Requires an <see cref="IConnectionMultiplexer"/> in DI.
    /// </summary>
    public static IServiceCollection AddWarpTalkPubSubLeadership(
        this IServiceCollection services,
        string resource,
        IReadOnlyCollection<string> requiredSubscriptions,
        Action<LeaderElectionOptions>? configure = null)
    {
        services.TryAddSingleton(sp => new PubSubSubscriptionEligibility(
            sp.GetRequiredService<IConnectionMultiplexer>(),
            requiredSubscriptions));
        services.TryAddSingleton<ILeaderEligibility>(sp => sp.GetRequiredService<PubSubSubscriptionEligibility>());
        services.AddWarpTalkLeaderElection(resource, configure);
        services.TryAddSingleton<IPubSubLeadership, PubSubLeadership>();
        return services;
    }
}
