using StackExchange.Redis;

namespace WarpTalk.Shared.Coordination;

/// <summary>
/// Redis implementation of <see cref="ILeaseStore"/>: <c>SET key token NX PX ttl</c> to take a
/// lease, and Lua compare-and-set scripts to renew and release it, so no holder can extend or
/// delete a lease that has already passed to someone else.
///
/// Key layout (the braces are a Redis Cluster hash tag, so the lease and its fence always live
/// in the same slot and the acquire script may touch both):
/// <list type="bullet">
/// <item><c>warptalk:lock:{resource}</c> — the lease; value = holder token, PX = lease duration.</item>
/// <item><c>warptalk:lock:{resource}:fence</c> — INCR'd on every successful acquire. Never
/// expires, so the fencing token keeps increasing across holders and restarts.</item>
/// </list>
/// </summary>
public sealed class RedisLeaseStore : ILeaseStore
{
    private const string AcquireScript = """
        if redis.call('SET', KEYS[1], ARGV[1], 'NX', 'PX', ARGV[2]) then
            return redis.call('INCR', KEYS[2])
        end
        return 0
        """;

    private const string RenewScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('PEXPIRE', KEYS[1], ARGV[2])
        end
        return 0
        """;

    private const string ReleaseScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        end
        return 0
        """;

    private readonly IConnectionMultiplexer _redis;

    public RedisLeaseStore(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public static RedisKey LeaseKey(string resource) => $"warptalk:lock:{{{resource}}}";

    public static RedisKey FenceKey(string resource) => $"warptalk:lock:{{{resource}}}:fence";

    public async Task<long?> TryAcquireAsync(string resource, string token, TimeSpan leaseDuration)
    {
        var result = await _redis.GetDatabase().ScriptEvaluateAsync(
            AcquireScript,
            [LeaseKey(resource), FenceKey(resource)],
            [token, ToMilliseconds(leaseDuration)]);

        var fence = (long)result;
        return fence > 0 ? fence : null;
    }

    public async Task<bool> TryRenewAsync(string resource, string token, TimeSpan leaseDuration)
    {
        var result = await _redis.GetDatabase().ScriptEvaluateAsync(
            RenewScript,
            [LeaseKey(resource)],
            [token, ToMilliseconds(leaseDuration)]);

        return (long)result == 1;
    }

    public async Task<bool> ReleaseAsync(string resource, string token)
    {
        var result = await _redis.GetDatabase().ScriptEvaluateAsync(
            ReleaseScript,
            [LeaseKey(resource)],
            [token]);

        return (long)result == 1;
    }

    private static long ToMilliseconds(TimeSpan duration)
        => Math.Max(1, (long)Math.Ceiling(duration.TotalMilliseconds));
}
