using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using WarpTalk.Gateway.Constants;

namespace WarpTalk.Gateway.Hubs;

[Authorize]
public class BillingHub : Hub
{
    private readonly IConnectionManager _connectionManager;
    private readonly ILogger<BillingHub> _logger;

    public BillingHub(IConnectionManager connectionManager, ILogger<BillingHub> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var userId = GetUserId();
        _connectionManager.AddConnection(userId, Context.ConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroupName(userId));
        _logger.LogInformation("BillingHub: User {UserId} connected (ConnectionId: {ConnectionId})", userId, Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = GetUserId();
        var isFullyOffline = _connectionManager.RemoveConnection(userId, Context.ConnectionId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, UserGroupName(userId));
        _logger.LogInformation("BillingHub: User {UserId} disconnected (FullyOffline: {FullyOffline})", userId, isFullyOffline);
        await base.OnDisconnectedAsync(exception);
    }

    private string GetUserId() =>
        Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? Context.User?.FindFirst("sub")?.Value
        ?? throw new HubException("User identity not found in token.");

    internal static string UserGroupName(string userId) =>
        string.Format(GatewayBillingConstants.UserGroupTemplate, userId);
}
