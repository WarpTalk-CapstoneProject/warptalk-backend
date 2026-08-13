using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using WarpTalk.AssistantService.API.Hubs;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;

namespace WarpTalk.AssistantService.API.Services;

public class AssistantNotifier : IAssistantNotifier
{
    private readonly IHubContext<AssistantHub> _hub;

    public AssistantNotifier(IHubContext<AssistantHub> hub)
    {
        _hub = hub;
    }

    private IClientProxy Group(Guid conversationId) =>
        _hub.Clients.Group(AssistantHub.GetConversationGroupName(conversationId));

    public Task BroadcastMessageStartedAsync(Guid conversationId, Guid messageId, CancellationToken ct = default) =>
        Group(conversationId).SendAsync("AssistantMessageStarted", new { conversationId, messageId }, ct);

    public Task BroadcastMessageChunkAsync(Guid conversationId, Guid messageId, string delta, CancellationToken ct = default) =>
        Group(conversationId).SendAsync("AssistantMessageChunk", new { conversationId, messageId, delta }, ct);

    public Task BroadcastToolCallStartedAsync(Guid conversationId, Guid messageId, string toolName, CancellationToken ct = default) =>
        Group(conversationId).SendAsync("AssistantToolCallStarted", new { conversationId, messageId, toolName }, ct);

    public Task BroadcastToolCallCompletedAsync(Guid conversationId, Guid messageId, string toolName, string status, CancellationToken ct = default) =>
        Group(conversationId).SendAsync("AssistantToolCallCompleted", new { conversationId, messageId, toolName, status }, ct);

    public Task BroadcastMessageCompletedAsync(Guid conversationId, AssistantMessageDto message, CancellationToken ct = default) =>
        Group(conversationId).SendAsync("AssistantMessageCompleted", message, ct);

    public Task BroadcastQuestionAsync(Guid conversationId, Guid messageId, string questionsJson, CancellationToken ct = default) =>
        Group(conversationId).SendAsync("AssistantQuestion", new { conversationId, messageId, questionsJson }, ct);

    public Task BroadcastMessageFailedAsync(Guid conversationId, Guid messageId, string error, CancellationToken ct = default) =>
        Group(conversationId).SendAsync("AssistantMessageFailed", new { conversationId, messageId, error }, ct);

    public Task BroadcastFollowUpMessageAsync(Guid conversationId, AssistantMessageDto message, CancellationToken ct = default) =>
        Group(conversationId).SendAsync("AssistantFollowUpMessage", message, ct);
}
