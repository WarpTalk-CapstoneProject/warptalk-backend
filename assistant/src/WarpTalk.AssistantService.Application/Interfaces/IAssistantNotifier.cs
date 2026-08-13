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
    /// <summary>
    /// The assistant is asking the user to choose, rather than guessing.
    ///
    /// Its own event because the payload is a UI, not prose: the client renders a card of
    /// options from <paramref name="questionsJson"/>. Folding it into a normal message would
    /// mean the client had to find questions inside free text, which is exactly the parsing
    /// nobody can make reliable.
    /// </summary>
    Task BroadcastQuestionAsync(Guid conversationId, Guid messageId, string questionsJson, CancellationToken ct = default);

    Task BroadcastMessageFailedAsync(Guid conversationId, Guid messageId, string error, CancellationToken ct = default);
    Task BroadcastFollowUpMessageAsync(Guid conversationId, AssistantMessageDto message, CancellationToken ct = default);
}
