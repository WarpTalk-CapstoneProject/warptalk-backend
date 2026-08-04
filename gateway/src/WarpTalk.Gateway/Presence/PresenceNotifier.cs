using Microsoft.AspNetCore.SignalR;
using WarpTalk.Gateway.Constants;
using WarpTalk.Gateway.Hubs;

namespace WarpTalk.Gateway.Presence;

/// <summary>
/// Applies a presence change and tells the workspaces that care about it.
///
/// Fanned out per workspace rather than broadcast globally: presence is only meaningful between
/// people who share a workspace, and a global broadcast would leak who is online across tenants.
/// </summary>
public interface IPresenceNotifier
{
    Task UserConnectedAsync(string userId, CancellationToken ct = default);
    Task UserDisconnectedAsync(string userId, CancellationToken ct = default);
    Task UserEnteredMeetingAsync(string userId, CancellationToken ct = default);
    Task UserLeftMeetingAsync(string userId, CancellationToken ct = default);

    /// <summary>Announces the user's current state into a workspace they just started watching.</summary>
    Task AnnounceToWorkspaceAsync(string userId, string workspaceId, CancellationToken ct = default);

    /// <summary>Records that this user is watching a workspace, so later changes reach it.</summary>
    Task TrackWorkspaceAsync(string userId, string workspaceId, CancellationToken ct = default);
}

public sealed class PresenceNotifier : IPresenceNotifier
{
    private readonly IPresenceStore _store;
    private readonly IHubContext<NotificationHub> _hub;
    private readonly ILogger<PresenceNotifier> _logger;

    public PresenceNotifier(
        IPresenceStore store,
        IHubContext<NotificationHub> hub,
        ILogger<PresenceNotifier> logger)
    {
        _store = store;
        _hub = hub;
        _logger = logger;
    }

    public async Task UserConnectedAsync(string userId, CancellationToken ct = default)
    {
        await _store.SetOnlineAsync(userId, ct);
        await BroadcastAsync(userId, ct);
    }

    public async Task UserDisconnectedAsync(string userId, CancellationToken ct = default)
    {
        // Read the workspaces before clearing: SetOfflineAsync drops them along with the state,
        // and after that there is nobody left to tell.
        var workspaces = await _store.GetTrackedWorkspacesAsync(userId, ct);
        await _store.SetOfflineAsync(userId, ct);
        await BroadcastAsync(userId, PresenceState.Offline, workspaces, ct);
    }

    public async Task UserEnteredMeetingAsync(string userId, CancellationToken ct = default)
    {
        await _store.SetInMeetingAsync(userId, ct);
        await BroadcastAsync(userId, ct);
    }

    public async Task UserLeftMeetingAsync(string userId, CancellationToken ct = default)
    {
        await _store.ClearInMeetingAsync(userId, ct);
        await BroadcastAsync(userId, ct);
    }

    public Task TrackWorkspaceAsync(string userId, string workspaceId, CancellationToken ct = default) =>
        _store.TrackWorkspaceAsync(userId, workspaceId, ct);

    public async Task AnnounceToWorkspaceAsync(string userId, string workspaceId, CancellationToken ct = default)
    {
        var state = (await _store.GetAsync([userId], ct))[userId];
        await SendAsync(userId, state, [workspaceId], ct);
    }

    private async Task BroadcastAsync(string userId, CancellationToken ct)
    {
        var state = (await _store.GetAsync([userId], ct))[userId];
        var workspaces = await _store.GetTrackedWorkspacesAsync(userId, ct);
        await BroadcastAsync(userId, state, workspaces, ct);
    }

    private Task BroadcastAsync(
        string userId,
        PresenceState state,
        IReadOnlyCollection<string> workspaces,
        CancellationToken ct) => SendAsync(userId, state, workspaces, ct);

    private async Task SendAsync(
        string userId,
        PresenceState state,
        IReadOnlyCollection<string> workspaces,
        CancellationToken ct)
    {
        if (workspaces.Count == 0) return;

        var payload = new { userId, state = state.ToString() };

        foreach (var workspaceId in workspaces)
        {
            try
            {
                await _hub.Clients
                    .Group(RealtimeConstants.Groups.Workspace(workspaceId))
                    .SendAsync(RealtimeConstants.ClientMethods.UserPresenceChanged, payload, ct);
            }
            catch (Exception ex)
            {
                // One workspace failing to receive a dot must not stop the others, and must
                // never fail the connection lifecycle call that triggered this.
                _logger.LogWarning(
                    ex,
                    "Could not announce presence for {UserId} to workspace {WorkspaceId}.",
                    userId,
                    workspaceId);
            }
        }
    }
}
