using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.Shared.Authorization;

namespace WarpTalk.AssistantService.API.Hubs;

[Authorize]
public class AssistantHub : Hub
{
    private readonly IAssistantConversationService _conversationService;
    private readonly IPlatformAssistantConversationService _platformConversationService;
    private readonly IAuthorizationService _authorizationService;

    public AssistantHub(
        IAssistantConversationService conversationService,
        IPlatformAssistantConversationService platformConversationService,
        IAuthorizationService authorizationService)
    {
        _conversationService = conversationService;
        _platformConversationService = platformConversationService;
        _authorizationService = authorizationService;
    }

    public async Task JoinConversation(Guid conversationId)
    {
        var userIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? Context.User?.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out var userId))
            throw new HubException("Unauthorized");

        var access = await _conversationService.AuthorizeConversationAccessAsync(
            conversationId, userId, Context.ConnectionAborted);
        if (!access.IsSuccess && !await CanJoinPlatformConversationAsync(conversationId, userId))
            throw new HubException("Forbidden: this conversation does not belong to you.");

        await Groups.AddToGroupAsync(Context.ConnectionId, GetConversationGroupName(conversationId));
    }

    /// <summary>
    /// A platform-scope conversation streams through the same group, but joining it needs the
    /// warpbot.use staff permission AS WELL AS ownership — the same gate its REST controller has.
    /// Evaluated with the real requirement rather than a role string, so the hub and the
    /// controller cannot drift into two different ideas of who may use it.
    /// </summary>
    private async Task<bool> CanJoinPlatformConversationAsync(Guid conversationId, Guid userId)
    {
        if (Context.User is null) return false;
        var mayUse = await _authorizationService.AuthorizeAsync(
            Context.User, resource: null, new PermissionRequirement(AdminPermissions.WarpBotUse));
        if (!mayUse.Succeeded) return false;

        var access = await _platformConversationService.AuthorizeConversationAccessAsync(
            conversationId, userId, Context.ConnectionAborted);
        return access.IsSuccess;
    }

    public async Task LeaveConversation(Guid conversationId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetConversationGroupName(conversationId));
    }

    public static string GetConversationGroupName(Guid conversationId) => $"assistant_conversation:{conversationId}";
}
