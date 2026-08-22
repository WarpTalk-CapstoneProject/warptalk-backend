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
    /// <param name="toolDetail">
    /// What the call is ABOUT — the phrase searched, the file opened, the site fetched. Empty
    /// when the call has no subject worth naming; never a placeholder. The tool name alone says
    /// a search happened, this says which one, which is what makes a wrong turn visible while
    /// it is still running.
    /// </param>
    Task BroadcastToolCallStartedAsync(Guid conversationId, Guid messageId, string toolName, string toolDetail, CancellationToken ct = default);
    Task BroadcastToolCallCompletedAsync(Guid conversationId, Guid messageId, string toolName, string status, string toolDetail, CancellationToken ct = default);

    /// <summary>
    /// The model's own account of the step it is taking, as a heading and the sentence under it.
    ///
    /// Distinct from a tool call and it has to be: a tool step says WHAT ran and never why, and
    /// between two calls — where the model is deciding what to do next — there is no tool to
    /// report at all. That gap is the longest silent stretch of a slow turn.
    /// </summary>
    Task BroadcastReasoningAsync(Guid conversationId, Guid messageId, string title, string body, CancellationToken ct = default);
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
