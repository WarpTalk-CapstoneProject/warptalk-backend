namespace WarpTalk.Shared.Coordination;

/// <summary>
/// A lease store that only coordinates holders inside ONE process.
///
/// Used where a service has no Redis configured (MeetingService treats Redis as optional for
/// local development), so the workers still run exactly as they did on a single instance instead
/// of never running at all. It gives no cross-replica guarantee whatsoever — a multi-replica
/// deployment must have Redis, and <see cref="CoordinationServiceCollectionExtensions"/> logs a
/// warning when it falls back to this. Also the store the lease unit tests drive, because expiry
/// here follows the injected <see cref="TimeProvider"/>.
/// </summary>
public sealed class InProcessLeaseStore : ILeaseStore
{
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Token, DateTimeOffset ExpiresAt)> _leases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _fences = new(StringComparer.Ordinal);

    public InProcessLeaseStore(TimeProvider time)
    {
        _time = time;
    }

    public Task<long?> TryAcquireAsync(string resource, string token, TimeSpan leaseDuration)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_leases.TryGetValue(resource, out var current) && current.ExpiresAt > now)
            {
                return Task.FromResult<long?>(null);
            }

            _leases[resource] = (token, now + leaseDuration);
            var fence = _fences.GetValueOrDefault(resource) + 1;
            _fences[resource] = fence;
            return Task.FromResult<long?>(fence);
        }
    }

    public Task<bool> TryRenewAsync(string resource, string token, TimeSpan leaseDuration)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_leases.TryGetValue(resource, out var current)
                && current.ExpiresAt > now
                && current.Token == token)
            {
                _leases[resource] = (token, now + leaseDuration);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }
    }

    public Task<bool> ReleaseAsync(string resource, string token)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_leases.TryGetValue(resource, out var current)
                && current.ExpiresAt > now
                && current.Token == token)
            {
                _leases.Remove(resource);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }
    }
}
