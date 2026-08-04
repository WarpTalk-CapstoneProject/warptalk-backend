using StackExchange.Redis;

namespace WarpTalk.Gateway.Presence;

public sealed class RedisPresenceStore : IPresenceStore
{
    /// <summary>
    /// How long a presence record outlives its last refresh. Comfortably longer than the
    /// heartbeat interval so an ordinary slow beat does not blink someone offline, short enough
    /// that a Gateway killed mid-flight does not strand people as online for long.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(90);

    private const string OnlineValue = "online";
    private const string InMeetingValue = "in_meeting";

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisPresenceStore> _logger;

    public RedisPresenceStore(IConnectionMultiplexer redis, ILogger<RedisPresenceStore> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    private static RedisKey StateKey(string userId) => $"warptalk:presence:{userId}";
    private static RedisKey WorkspacesKey(string userId) => $"warptalk:presence:workspaces:{userId}";

    public async Task SetOnlineAsync(string userId, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var key = StateKey(userId);

        // Only claims the key when nothing holds it, then refreshes the TTL either way. A second
        // tab opening must not pull someone out of InMeeting back to Online.
        await db.StringSetAsync(key, OnlineValue, Ttl, When.NotExists);
        await db.KeyExpireAsync(key, Ttl);
    }

    public async Task SetInMeetingAsync(string userId, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        await db.StringSetAsync(StateKey(userId), InMeetingValue, Ttl);
    }

    public async Task ClearInMeetingAsync(string userId, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var key = StateKey(userId);

        // Guarded on the current value: if the record is already gone the user left the app
        // entirely, and writing "online" here would resurrect them.
        var current = await db.StringGetAsync(key);
        if (current != InMeetingValue) return;

        await db.StringSetAsync(key, OnlineValue, Ttl);
    }

    public Task SetOfflineAsync(string userId, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        return Task.WhenAll(
            db.KeyDeleteAsync(StateKey(userId)),
            db.KeyDeleteAsync(WorkspacesKey(userId)));
    }

    public async Task RefreshAsync(string userId, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        await Task.WhenAll(
            db.KeyExpireAsync(StateKey(userId), Ttl),
            db.KeyExpireAsync(WorkspacesKey(userId), Ttl));
    }

    public async Task<IReadOnlyDictionary<string, PresenceState>> GetAsync(
        IReadOnlyCollection<string> userIds,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, PresenceState>(userIds.Count);
        if (userIds.Count == 0) return result;

        try
        {
            var db = _redis.GetDatabase();
            var keys = userIds.Select(StateKey).ToArray();
            var values = await db.StringGetAsync(keys);

            var index = 0;
            foreach (var userId in userIds)
            {
                result[userId] = Parse(values[index]);
                index++;
            }
        }
        catch (Exception ex)
        {
            // Presence is decoration. An unreachable Redis must not take the Members page with
            // it — everyone reads as offline until it recovers.
            _logger.LogWarning(ex, "Presence lookup failed for {Count} users; reporting offline.", userIds.Count);
            foreach (var userId in userIds)
            {
                result[userId] = PresenceState.Offline;
            }
        }

        return result;
    }

    public async Task TrackWorkspaceAsync(string userId, string workspaceId, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var key = WorkspacesKey(userId);
        await db.SetAddAsync(key, workspaceId);
        await db.KeyExpireAsync(key, Ttl);
    }

    public async Task<IReadOnlyCollection<string>> GetTrackedWorkspacesAsync(
        string userId,
        CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var members = await db.SetMembersAsync(WorkspacesKey(userId));
            return members.Select(m => m.ToString()).ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve tracked workspaces for {UserId}.", userId);
            return Array.Empty<string>();
        }
    }

    private static PresenceState Parse(RedisValue value) => value.ToString() switch
    {
        InMeetingValue => PresenceState.InMeeting,
        OnlineValue => PresenceState.Online,
        _ => PresenceState.Offline,
    };
}
