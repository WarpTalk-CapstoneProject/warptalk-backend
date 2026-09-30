using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace WarpTalk.Shared.Coordination;

/// <summary>Whether THIS replica is currently the single leader for a role.</summary>
public interface ILeaderElection
{
    /// <summary>The lease name the replicas compete for.</summary>
    string Resource { get; }

    /// <summary>
    /// True only while this replica holds the lease and its local deadline has not passed (see
    /// <see cref="IDistributedLease.IsHeld"/>). Cheap: no I/O. Check it immediately before the
    /// guarded action, not once at the start of a long operation.
    /// </summary>
    bool IsLeader { get; }

    /// <summary>The fencing token of the current term, or <c>null</c> when not leader.</summary>
    long? FencingToken { get; }
}

/// <summary>
/// Optional gate on standing for election. A replica that cannot do the leader's job — e.g. its
/// pub/sub subscriptions are not registered yet, or its subscriber connection is down — must not
/// take or keep the lease, or it would hold leadership while delivering nothing.
/// </summary>
public interface ILeaderEligibility
{
    bool IsEligible { get; }
}

public sealed class LeaderElectionOptions
{
    /// <summary>Lease name, e.g. <c>gateway:realtime-relay</c>.</summary>
    public string Resource { get; set; } = string.Empty;

    /// <summary>How long a lease survives without renewal. Bounds the failover gap after a crash.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Leader renews this often — several attempts fit inside one lease.</summary>
    public TimeSpan RenewInterval { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>Followers try to take the lease this often (and immediately on a release notice).</summary>
    public TimeSpan AcquireRetryInterval { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Lease-based leader election over <see cref="IDistributedLockProvider"/>, run as a hosted
/// service. Every replica runs one; at most one holds the lease at a time.
///
/// Guarantees (with the Redis lease store):
/// <list type="bullet">
/// <item>At most one replica reports <see cref="IsLeader"/> at any instant, provided a process is
/// not paused for longer than the lease safety margin between reading <see cref="IsLeader"/> and
/// acting on it (the leader's local deadline expires before Redis lets anyone else in).</item>
/// <item>Crash of the leader: another eligible replica takes over within LeaseDuration +
/// AcquireRetryInterval (5s + 1s by default).</item>
/// <item>Graceful shutdown of the leader: the lease is released and a notice published on
/// <c>warptalk:lock:{resource}:released</c>; followers retry at once, so the handover takes one
/// Redis round trip rather than a lease timeout.</item>
/// <item>Redis unreachable: the leader keeps leading until its local deadline, then steps down;
/// nobody leads until Redis returns (fail closed — no duplicate leaders).</item>
/// </list>
/// Nothing escapes <see cref="ExecuteAsync"/>: an exception here would stop the whole host.
/// </summary>
public sealed class LeaderElector : BackgroundService, ILeaderElection
{
    private readonly IDistributedLockProvider _locks;
    private readonly LeaderElectionOptions _options;
    private readonly ILeaderEligibility? _eligibility;
    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<LeaderElector> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private IDistributedLease? _lease;

    public LeaderElector(
        IDistributedLockProvider locks,
        LeaderElectionOptions options,
        ILogger<LeaderElector> logger,
        ILeaderEligibility? eligibility = null,
        IConnectionMultiplexer? redis = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Resource);
        if (options.RenewInterval >= options.LeaseDuration)
        {
            throw new ArgumentException("RenewInterval must be shorter than LeaseDuration.", nameof(options));
        }

        _locks = locks;
        _options = options;
        _logger = logger;
        _eligibility = eligibility;
        _redis = redis;
    }

    public string Resource => _options.Resource;

    public bool IsLeader => Volatile.Read(ref _lease)?.IsHeld == true;

    public long? FencingToken => Volatile.Read(ref _lease) is { IsHeld: true } lease ? lease.FencingToken : null;

    public static RedisChannel ReleasedChannel(string resource)
        => RedisChannel.Literal($"warptalk:lock:{{{resource}}}:released");

    /// <summary>
    /// One election step: renew if leading, otherwise try to take the lease. Public so tests can
    /// drive the state machine deterministically; the hosted loop just calls it on a timer.
    /// Never throws except for cancellation.
    /// </summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        await _tickGate.WaitAsync(cancellationToken);
        try
        {
            await TickCoreAsync(cancellationToken);
        }
        finally
        {
            _tickGate.Release();
        }
    }

    private async Task TickCoreAsync(CancellationToken cancellationToken)
    {
        var eligible = IsEligibleSafe();
        var lease = _lease;

        if (lease is not null)
        {
            if (!eligible)
            {
                _logger.LogWarning(
                    "Leader for {Resource} (fence {Fence}) is no longer eligible; stepping down.",
                    Resource, lease.FencingToken);
                await ReleaseAsync(lease, publishNotice: true);
                return;
            }

            try
            {
                if (await lease.TryRenewAsync(cancellationToken))
                {
                    return;
                }

                SetLease(null);
                _logger.LogWarning(
                    "Lost leadership of {Resource} (fence {Fence}): the lease expired or was taken over.",
                    Resource, lease.FencingToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (lease.IsHeld)
                {
                    _logger.LogWarning(ex, "Could not renew leadership of {Resource}; still within the lease deadline.", Resource);
                    return;
                }

                SetLease(null);
                _logger.LogWarning(ex, "Stepping down as leader of {Resource}: renewal failed past the lease deadline.", Resource);
            }
        }

        if (!eligible || _lease is not null)
        {
            return;
        }

        try
        {
            var acquired = await _locks.TryAcquireAsync(Resource, _options.LeaseDuration, cancellationToken);
            if (acquired is not null)
            {
                SetLease(acquired);
                _logger.LogInformation(
                    "This replica is now leader for {Resource} (fence {Fence}).",
                    Resource, acquired.FencingToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not contend for leadership of {Resource}; will retry.", Resource);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SubscribeToReleaseNoticesAsync();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
                var delay = IsLeader ? _options.RenewInterval : _options.AcquireRetryInterval;
                await _wake.WaitAsync(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // TickAsync already swallows store failures; this is the last line of defence.
                _logger.LogError(ex, "Leader election loop for {Resource} failed; retrying.", Resource);
                try
                {
                    await Task.Delay(_options.AcquireRetryInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        var lease = _lease;
        if (lease is not null)
        {
            _logger.LogInformation("Releasing leadership of {Resource} on shutdown.", Resource);
            await ReleaseAsync(lease, publishNotice: true);
        }
    }

    private async Task ReleaseAsync(IDistributedLease lease, bool publishNotice)
    {
        SetLease(null);
        try
        {
            await lease.ReleaseAsync();
            if (publishNotice && _redis is not null)
            {
                await _redis.GetSubscriber().PublishAsync(ReleasedChannel(Resource), lease.FencingToken);
            }
        }
        catch (Exception ex)
        {
            // The lease will simply expire; followers take over after LeaseDuration instead.
            _logger.LogWarning(ex, "Could not release leadership of {Resource} cleanly; it will expire.", Resource);
        }
    }

    private async Task SubscribeToReleaseNoticesAsync()
    {
        if (_redis is null)
        {
            return;
        }

        try
        {
            await _redis.GetSubscriber().SubscribeAsync(ReleasedChannel(Resource), (_, _) => Wake());
        }
        catch (Exception ex)
        {
            // Only an optimisation: without it a follower still retries every AcquireRetryInterval.
            _logger.LogWarning(ex, "Could not subscribe to release notices for {Resource}; failover falls back to polling.", Resource);
        }
    }

    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // Already awake.
        }
    }

    private bool IsEligibleSafe()
    {
        try
        {
            return _eligibility?.IsEligible ?? true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Eligibility check for {Resource} threw; treating this replica as ineligible.", Resource);
            return false;
        }
    }

    private void SetLease(IDistributedLease? lease) => Volatile.Write(ref _lease, lease);

    public override void Dispose()
    {
        _wake.Dispose();
        _tickGate.Dispose();
        base.Dispose();
    }
}
