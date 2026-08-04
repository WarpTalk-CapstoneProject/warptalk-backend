using WarpTalk.Gateway.Hubs;

namespace WarpTalk.Gateway.Presence;

/// <summary>
/// Keeps the presence records of currently-connected users from expiring.
///
/// The TTL on each record is what makes presence self-healing: a Gateway that dies without
/// running OnDisconnectedAsync leaves nothing behind that outlives it. That only works if a
/// living Gateway keeps re-asserting the users it still holds sockets for, which is this.
/// </summary>
public sealed class PresenceHeartbeatService : BackgroundService
{
    // A third of the TTL, so a single missed beat is not enough to blink anyone offline.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly IConnectionManager _connections;
    private readonly IPresenceStore _store;
    private readonly ILogger<PresenceHeartbeatService> _logger;

    public PresenceHeartbeatService(
        IConnectionManager connections,
        IPresenceStore store,
        ILogger<PresenceHeartbeatService> logger)
    {
        _connections = connections;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                foreach (var userId in _connections.GetOnlineUserIds())
                {
                    await _store.RefreshAsync(userId, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let a bad beat kill the loop — the next one will re-assert everyone,
                // and the TTL is long enough to absorb a miss.
                _logger.LogWarning(ex, "Presence heartbeat failed; retrying next tick.");
            }
        }
    }
}
