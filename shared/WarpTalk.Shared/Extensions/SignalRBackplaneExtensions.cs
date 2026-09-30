using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.Configuration;

namespace WarpTalk.Shared.Extensions;

/// <summary>
/// Shared rules for running a SignalR hub behind more than one replica. The backplane package
/// itself (Microsoft.AspNetCore.SignalR.StackExchangeRedis) is referenced only by the API projects
/// that host a hub, so it does not ride into every service through this shared project.
/// </summary>
public static class SignalRBackplaneExtensions
{
    /// <summary>
    /// The Redis connection string a hub's backplane must use, or <c>null</c> for single-node
    /// SignalR (no Redis configured at all).
    ///
    /// Reads <c>SignalR:Redis</c> (env <c>SignalR__Redis</c>, the gateway's key) and falls back to
    /// <c>Redis:ConnectionString</c>, then <c>ConnectionStrings:Redis</c>. The fallback matters:
    /// without a backplane, N replicas silently deliver each <c>IHubContext</c> send only to the
    /// ~1/N of clients that share a pod with the sender, and nothing errors.
    ///
    /// Callers pass the result to <c>AddStackExchangeRedis</c> with a per-service channel prefix
    /// (so two services' hubs never share Redis channels) and <c>AbortOnConnectFail = false</c>
    /// (a Redis outage degrades the instance to single-node SignalR instead of stopping it from
    /// booting).
    /// </summary>
    public static string? ResolveBackplaneConnectionString(IConfiguration configuration)
    {
        foreach (var candidate in new[]
                 {
                     configuration["SignalR:Redis"],
                     configuration["Redis:ConnectionString"],
                     configuration.GetConnectionString("Redis"),
                 })
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// For hubs reached through the gateway's YARP proxy (meeting chat, assistant), where nothing
    /// pins a client to one pod: YARP forwards negotiate and the WebSocket upgrade as two separate
    /// requests to the Kubernetes Service, which may pick two different pods, and the second pod
    /// has never heard of the connection id the first one issued.
    ///
    /// Restricting the server to WebSockets removes Long Polling (every poll must land on the same
    /// pod, which cannot hold there at all). The client must also connect with
    /// <c>skipNegotiation: true</c> so the WebSocket request itself creates the connection on
    /// whichever pod receives it — that half is in the web client.
    /// </summary>
    public static void UseWebSocketsOnly(HttpConnectionDispatcherOptions options)
    {
        options.Transports = HttpTransportType.WebSockets;
    }
}
