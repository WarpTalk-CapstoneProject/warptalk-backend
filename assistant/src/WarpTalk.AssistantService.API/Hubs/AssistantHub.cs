using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using WarpTalk.AssistantService.Application.Interfaces;

namespace WarpTalk.AssistantService.API.Hubs;

[Authorize]
public class AssistantHub : Hub
{
    private readonly IAssistantConversationService _conversationService;

    public AssistantHub(IAssistantConversationService conversationService)
    {
        _conversationService = conversationService;
    }

    public async Task JoinConversation(Guid conversationId)
    {
        var userIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? Context.User?.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out var userId))
            throw new HubException("Unauthorized");

        var access = await _conversationService.AuthorizeConversationAccessAsync(
            conversationId, userId, Context.ConnectionAborted);
        if (!access.IsSuccess)
            throw new HubException("Forbidden: this conversation does not belong to you.");

        await Groups.AddToGroupAsync(Context.ConnectionId, GetConversationGroupName(conversationId));
    }

    public async Task LeaveConversation(Guid conversationId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetConversationGroupName(conversationId));
    }

    public static string GetConversationGroupName(Guid conversationId) => $"assistant_conversation:{conversationId}";
}
