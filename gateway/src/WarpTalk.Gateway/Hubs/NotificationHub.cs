using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;
using WarpTalk.Gateway.Presence;

namespace WarpTalk.Gateway.Hubs;

/// <summary>
/// Real-time notification push hub.
/// Each user auto-joins their personal group: "user:{userId}".
/// This enables server-to-client pushes for notifications from any service.
/// </summary>
[Authorize]
public class NotificationHub : Hub
{
    private readonly IConnectionManager _connectionManager;
    private readonly IPresenceNotifier _presence;
    private readonly ILogger<NotificationHub> _logger;
    private readonly WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient _grpcClient;

    public NotificationHub(
        IConnectionManager connectionManager,
        IPresenceNotifier presence,
        ILogger<NotificationHub> logger,
        WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient grpcClient)
    {
        _connectionManager = connectionManager;
        _presence = presence;
        _logger = logger;
        _grpcClient = grpcClient;
    }

    // ── Lifecycle ─────────────────────────────────────────

    public override async Task OnConnectedAsync()
    {
        var userId = GetUserId();
        _connectionManager.AddConnection(userId, Context.ConnectionId);

        // Automatically subscribe to the user's personal notification group
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroupName(userId));

        // This hub is the one every signed-in client holds open, which makes its connection
        // lifecycle the truthful signal for "is this member reachable".
        await _presence.UserConnectedAsync(userId, Context.ConnectionAborted);

        _logger.LogInformation(
            "NotificationHub: User {UserId} connected (ConnectionId: {ConnectionId})",
            userId, Context.ConnectionId);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = GetUserId();
        var isFullyOffline = _connectionManager.RemoveConnection(userId, Context.ConnectionId);

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, UserGroupName(userId));

        // Only when the LAST connection goes: closing one of three tabs does not make someone
        // offline, which is exactly what RemoveConnection's return value already tracks.
        if (isFullyOffline)
        {
            await _presence.UserDisconnectedAsync(userId);
        }

        _logger.LogInformation(
            "NotificationHub: User {UserId} disconnected (FullyOffline: {FullyOffline})",
            userId, isFullyOffline);

        await base.OnDisconnectedAsync(exception);
    }

    // ── Server Methods (Client → Server) ──────────────────


    public async Task MarkAsRead(Guid notificationId)
    {
        var userId = GetUserId();

        // Call NotificationService via gRPC to persist read status
        var request = new WarpTalk.Shared.Protos.MarkAsReadRequest
        {
            UserId = userId,
            NotificationId = notificationId.ToString()
        };

        try
        {
            var response = await _grpcClient.MarkAsReadAsync(request);
            if (response.Success)
            {
                // Broadcast the read event to all user's connections
                await Clients.Group(UserGroupName(userId))
                    .SendAsync("NotificationRead", notificationId);

                _logger.LogDebug(
                    "NotificationHub: User {UserId} marked notification {NotificationId} as read",
                    userId, notificationId);
            }
            else
            {
                _logger.LogWarning("NotificationHub: Failed to mark {NotificationId} as read. Reason: {Reason}", notificationId, response.ErrorMessage);
                // Can also send error back to client
                await Clients.Caller.SendAsync("NotificationError", $"Failed to mark as read: {response.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NotificationHub: Error calling gRPC MarkAsRead for user {UserId}", userId);
            await Clients.Caller.SendAsync("NotificationError", "An error occurred while marking as read.");
        }
    }


    public async Task MarkAllAsRead()
    {
        var userId = GetUserId();

        // Call NotificationService via gRPC to persist
        var request = new WarpTalk.Shared.Protos.MarkAllAsReadRequest { UserId = userId };
        try
        {
            var response = await _grpcClient.MarkAllAsReadAsync(request);

            if (response.Success)
            {
                await Clients.Group(UserGroupName(userId))
                    .SendAsync("AllNotificationsRead");

                _logger.LogDebug(
                    "NotificationHub: User {UserId} marked all notifications as read",
                    userId);
            }
            else
            {
                _logger.LogWarning("NotificationHub: Failed to mark all as read. Reason: {Reason}", response.ErrorMessage);
                await Clients.Caller.SendAsync("NotificationError", $"Failed to mark all as read: {response.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NotificationHub: Error calling gRPC MarkAllAsRead for user {UserId}", userId);
            await Clients.Caller.SendAsync("NotificationError", "An error occurred while marking all as read.");
        }
    }

    public async Task SubscribeWorkspace(string workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)) return;
        await Groups.AddToGroupAsync(Context.ConnectionId, WorkspaceGroupName(workspaceId));

        var userId = GetUserId();
        // Presence changes fan out per workspace, so the store has to know which ones this user
        // belongs to. Announcing right after joining also tells the room the caller is here —
        // the members already on the page learn about a late arrival without refetching.
        await _presence.TrackWorkspaceAsync(userId, workspaceId, Context.ConnectionAborted);
        await _presence.AnnounceToWorkspaceAsync(userId, workspaceId, Context.ConnectionAborted);

        _logger.LogDebug("NotificationHub: Connection {ConnectionId} joined group {GroupName}", Context.ConnectionId, WorkspaceGroupName(workspaceId));
    }

    public async Task UnsubscribeWorkspace(string workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)) return;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, WorkspaceGroupName(workspaceId));
        _logger.LogDebug("NotificationHub: Connection {ConnectionId} left group {GroupName}", Context.ConnectionId, WorkspaceGroupName(workspaceId));
    }

    // ── Helpers ────────────────────────────────────────────

    private string GetUserId() =>
        Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? Context.User?.FindFirst("sub")?.Value
        ?? throw new HubException("User identity not found in token.");

    private static string UserGroupName(string userId) => $"user:{userId}";
    private static string WorkspaceGroupName(string workspaceId) => $"workspace:{workspaceId}";
}
