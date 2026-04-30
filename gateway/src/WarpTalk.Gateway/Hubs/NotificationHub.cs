using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

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
    private readonly ILogger<NotificationHub> _logger;
    private readonly WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient _grpcClient;

    public NotificationHub(
        IConnectionManager connectionManager, 
        ILogger<NotificationHub> logger,
        WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient grpcClient)
    {
        _connectionManager = connectionManager;
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

        _logger.LogInformation(
            "NotificationHub: User {UserId} disconnected (FullyOffline: {FullyOffline})",
            userId, isFullyOffline);

        await base.OnDisconnectedAsync(exception);
    }

    // ── Server Methods (Client → Server) ──────────────────

    /// <summary>
    /// Mark a notification as read. Broadcasts to all user's devices
    /// so the read status is synced across tabs/devices.
    /// </summary>
    public async Task MarkAsRead(Guid notificationId)
    {
        var userId = GetUserId();

        // Call NotificationService via gRPC to persist read status
        var request = new WarpTalk.Shared.Protos.MarkAsReadRequest
        {
            UserId = userId,
            NotificationId = notificationId.ToString()
        };

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
        }
    }

    /// <summary>
    /// Mark all notifications as read.
    /// </summary>
    public async Task MarkAllAsRead()
    {
        var userId = GetUserId();

        // Call NotificationService via gRPC to persist
        var request = new WarpTalk.Shared.Protos.MarkAllAsReadRequest { UserId = userId };
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
        }
    }

    // ── Helpers ────────────────────────────────────────────

    private string GetUserId() =>
        Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? Context.User?.FindFirst("sub")?.Value
        ?? throw new HubException("User identity not found in token.");

    private static string UserGroupName(string userId) => $"user:{userId}";
}
