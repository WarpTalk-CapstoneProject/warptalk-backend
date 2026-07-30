using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AssistantService.Application.DTOs;

namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>Pushes agent-loop progress to the conversation's SignalR group (AssistantHub).</summary>
public interface IAssistantNotifier
{
    Task BroadcastMessageStartedAsync(Guid conversationId, Guid messageId, CancellationToken ct = default);
    Task BroadcastMessageChunkAsync(Guid conversationId, Guid messageId, string delta, CancellationToken ct = default);
    Task BroadcastToolCallStartedAsync(Guid conversationId, Guid messageId, string toolName, CancellationToken ct = default);
    Task BroadcastToolCallCompletedAsync(Guid conversationId, Guid messageId, string toolName, string status, CancellationToken ct = default);
    Task BroadcastMessageCompletedAsync(Guid conversationId, AssistantMessageDto message, CancellationToken ct = default);
    Task BroadcastMessageFailedAsync(Guid conversationId, Guid messageId, string error, CancellationToken ct = default);
    Task BroadcastFollowUpMessageAsync(Guid conversationId, AssistantMessageDto message, CancellationToken ct = default);
}
