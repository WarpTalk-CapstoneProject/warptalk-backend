using StackExchange.Redis;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Shared;

namespace WarpTalk.Gateway.Presence;

/// <summary>
/// Tells the room service which rooms still have a live hub socket on them — see
/// <see cref="RoomHubLiveness"/> for why the abandoned-room reapers need to know.
///
/// The room-scoped sibling of <see cref="PresenceHeartbeatService"/>, and self-healing the same
/// way: every replica re-asserts only the rooms it holds connections on, the keys expire on their
/// own, and nothing depends on OnDisconnectedAsync ever running. A replica that is OOM-killed mid
/// meeting simply stops asserting, which is precisely the case participant-offline cannot report.
/// </summary>
public sealed class RoomLivenessHeartbeatService : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RoomLivenessHeartbeatService> _logger;

    public RoomLivenessHeartbeatService(
        IConnectionMultiplexer redis,
        ILogger<RoomLivenessHeartbeatService> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RoomHubLiveness.BeatInterval);

        // Beat once before the first tick, so a freshly started replica re-asserts the rooms its
        // reconnecting clients rejoin without waiting a full interval.
        do
        {
            try
            {
                await BeatAsync(
                    _redis.GetDatabase(),
                    TranslationRoomHub.RoomsWithConnections(),
                    DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A missed beat costs nothing: the TTL spans several, and a heartbeat that stays
                // down lets its marker lapse, after which the reapers go back to trusting rows.
                _logger.LogWarning(ex, "Room liveness heartbeat failed; retrying next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// One beat. Room keys first, then the marker, so a reader that sees this beat's marker can
    /// never be looking at a room this beat has not reached yet.
    /// </summary>
    public static async Task BeatAsync(IDatabase db, IReadOnlyCollection<string> roomIds, DateTimeOffset now)
    {
        var stamp = now.ToUnixTimeMilliseconds();

        await Task.WhenAll(roomIds.Select(roomId =>
            db.StringSetAsync(RoomHubLiveness.RoomKey(roomId), stamp, RoomHubLiveness.Ttl)));

        // NX keeps the value at the start of the unbroken run — the reader's warm-up is measured
        // from it — and the EXPIRE is what keeps the run unbroken.
        await db.StringSetAsync(RoomHubLiveness.HeartbeatKey, stamp, RoomHubLiveness.Ttl, When.NotExists);
        await db.KeyExpireAsync(RoomHubLiveness.HeartbeatKey, RoomHubLiveness.Ttl);
    }
}
