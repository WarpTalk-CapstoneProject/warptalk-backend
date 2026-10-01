namespace WarpTalk.Gateway.Presence;

/// <summary>
/// Per-connection budget for <c>NotificationHub.QueryPresence</c>.
///
/// WHY IT EXISTS. <c>POST /api/v1/presence/query</c> sat behind the gateway's global HTTP rate
/// limiter. A hub method does not: <c>app.UseRateLimiter()</c> sees the WebSocket upgrade once and
/// never the invocations that flow over it afterwards. On 2026-09-24 a client-side retry loop on
/// the REST lookup spent an account's whole 180/min budget (web #562). Moved onto the hub with no
/// budget at all, the same loop would instead land on WorkspaceService's shared-membership gRPC and
/// Redis, unthrottled. This puts a ceiling back.
///
/// WHY PER CONNECTION. The state lives in <c>HubCallerContext.Items</c>, which belongs to the
/// connection and therefore to the one gateway replica holding it (negotiate and the socket are
/// pinned together by the sticky cookie). No shared counter, no cross-replica coordination, and
/// nothing to leak when the connection closes. A user with several tabs gets a budget per tab,
/// which is the right unit: the loop being contained is a per-tab render loop.
///
/// SignalR runs one invocation at a time per connection by default
/// (<c>MaximumParallelInvocationsPerClient = 1</c>); the lock is there so that raising that setting
/// later cannot quietly make the counter racy.
/// </summary>
public sealed class PresenceQueryThrottle
{
    /// <summary>
    /// Calls allowed per window. The web client batches every id asked for in one tick into one
    /// call and backs a failed id off for 60s, so a page costs one call and a reconnect one more;
    /// 30 a minute is ample for real use and a hard stop for a loop.
    /// </summary>
    public const int MaxCallsPerWindow = 30;

    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private static readonly object ItemsKey = typeof(PresenceQueryThrottle);

    private readonly object _gate = new();
    private DateTimeOffset _windowStart = DateTimeOffset.MinValue;
    private int _calls;

    /// <summary>The throttle for this connection, created on first use.</summary>
    public static PresenceQueryThrottle For(IDictionary<object, object?> connectionItems)
    {
        lock (connectionItems)
        {
            if (connectionItems.TryGetValue(ItemsKey, out var existing) && existing is PresenceQueryThrottle throttle)
            {
                return throttle;
            }

            var created = new PresenceQueryThrottle();
            connectionItems[ItemsKey] = created;
            return created;
        }
    }

    /// <summary>True when a call may proceed at <paramref name="now"/>; counts it if so.</summary>
    public bool TryAcquire(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (now - _windowStart >= Window || now < _windowStart)
            {
                _windowStart = now;
                _calls = 0;
            }

            if (_calls >= MaxCallsPerWindow)
            {
                return false;
            }

            _calls++;
            return true;
        }
    }
}
