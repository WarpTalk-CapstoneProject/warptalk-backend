namespace WarpTalk.Shared.Coordination;

/// <summary>Hands out cluster-wide leases. One per process; register via
/// <see cref="CoordinationServiceCollectionExtensions.AddWarpTalkDistributedLocks"/>.</summary>
public interface IDistributedLockProvider
{
    /// <summary>
    /// Takes the lease on <paramref name="resource"/> for <paramref name="leaseDuration"/>, or
    /// returns <c>null</c> when another holder has it. Throws when the store is unreachable — a
    /// caller that cannot tell whether somebody else holds the lease must not act as if it did.
    /// </summary>
    Task<IDistributedLease?> TryAcquireAsync(string resource, TimeSpan leaseDuration, CancellationToken cancellationToken = default);
}

/// <summary>A held lease. Dispose (or <see cref="ReleaseAsync"/>) to give it back early.</summary>
public interface IDistributedLease : IAsyncDisposable
{
    string Resource { get; }

    /// <summary>Random per-acquisition token; the only thing renew/release will act on.</summary>
    string Token { get; }

    /// <summary>
    /// Strictly increasing per resource across all holders. A newer holder always has a larger
    /// value, so anything that records it can reject work from a holder that was superseded.
    /// </summary>
    long FencingToken { get; }

    /// <summary>
    /// Whether this process may still act as the holder RIGHT NOW. Computed locally from the time
    /// the last successful acquire/renew was SENT plus the lease duration, minus a safety margin
    /// — so it turns false before the store can expire the key and let anyone else in, even if
    /// this process can no longer reach the store to find out.
    /// </summary>
    bool IsHeld { get; }

    /// <summary>
    /// Extends the lease. <c>false</c> = definitively lost (expired or taken by another holder);
    /// <see cref="IsHeld"/> is false from then on. Throws when the store is unreachable, in which
    /// case the lease is still held until its local deadline passes.
    /// </summary>
    Task<bool> TryRenewAsync(CancellationToken cancellationToken = default);

    /// <summary>Gives the lease back if (and only if) this holder still owns it.</summary>
    Task<bool> ReleaseAsync();
}

/// <summary>
/// Default <see cref="IDistributedLockProvider"/>: leases over any <see cref="ILeaseStore"/>
/// (Redis in production), with the local validity deadline described on
/// <see cref="IDistributedLease.IsHeld"/>.
/// </summary>
public sealed class DistributedLockProvider : IDistributedLockProvider
{
    private readonly ILeaseStore _store;
    private readonly TimeProvider _time;

    public DistributedLockProvider(ILeaseStore store, TimeProvider time)
    {
        _store = store;
        _time = time;
    }

    /// <summary>
    /// How much earlier than the store's expiry a holder stops trusting its lease: a fifth of the
    /// lease, capped at one second. Covers clock-rate drift between this host and Redis and the
    /// time between checking <see cref="IDistributedLease.IsHeld"/> and the guarded action.
    /// </summary>
    public static TimeSpan SafetyMarginFor(TimeSpan leaseDuration)
        => TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(1).Ticks, leaseDuration.Ticks / 5));

    public async Task<IDistributedLease?> TryAcquireAsync(
        string resource,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "Lease duration must be positive.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var token = Guid.NewGuid().ToString("N");
        // Taken BEFORE the command is sent: the store starts its TTL no earlier than this, so a
        // deadline measured from here can only be conservative.
        var sentAt = _time.GetTimestamp();
        var fence = await _store.TryAcquireAsync(resource, token, leaseDuration);
        if (fence is null)
        {
            return null;
        }

        return new Lease(_store, _time, resource, token, fence.Value, leaseDuration, sentAt);
    }

    private sealed class Lease : IDistributedLease
    {
        private readonly ILeaseStore _store;
        private readonly TimeProvider _time;
        private readonly TimeSpan _leaseDuration;
        private readonly TimeSpan _trustedFor;
        private long _validFromTimestamp;
        private volatile bool _gone;

        public Lease(ILeaseStore store, TimeProvider time, string resource, string token, long fence, TimeSpan leaseDuration, long sentAt)
        {
            _store = store;
            _time = time;
            Resource = resource;
            Token = token;
            FencingToken = fence;
            _leaseDuration = leaseDuration;
            _trustedFor = leaseDuration - SafetyMarginFor(leaseDuration);
            _validFromTimestamp = sentAt;
        }

        public string Resource { get; }
        public string Token { get; }
        public long FencingToken { get; }

        public bool IsHeld
            => !_gone && _time.GetElapsedTime(Interlocked.Read(ref _validFromTimestamp)) < _trustedFor;

        public async Task<bool> TryRenewAsync(CancellationToken cancellationToken = default)
        {
            if (_gone)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var sentAt = _time.GetTimestamp();
            var renewed = await _store.TryRenewAsync(Resource, Token, _leaseDuration);
            if (renewed)
            {
                Interlocked.Exchange(ref _validFromTimestamp, sentAt);
                return true;
            }

            _gone = true;
            return false;
        }

        public async Task<bool> ReleaseAsync()
        {
            if (_gone)
            {
                return false;
            }

            // Stop acting as the holder before the network round trip, not after it.
            _gone = true;
            return await _store.ReleaseAsync(Resource, Token);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await ReleaseAsync();
            }
            catch
            {
                // Best effort: an unreleased lease simply expires after its duration.
            }
        }
    }
}
