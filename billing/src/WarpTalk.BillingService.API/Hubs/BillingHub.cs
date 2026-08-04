using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.API.Hubs;

[Authorize]
public class BillingHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = GetUserId();
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroupName(userId));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = GetUserId();
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, UserGroupName(userId));
        await base.OnDisconnectedAsync(exception);
    }

    private string GetUserId() =>
        Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? Context.User?.FindFirst("sub")?.Value
        ?? throw new HubException("User identity not found in token.");

    internal static string UserGroupName(string userId) =>
        string.Format(BillingMessageConstants.Notifications.HubGroups.UserGroupTemplate, userId);
}
