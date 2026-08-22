using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.MeetingService.Application.DTOs;

namespace WarpTalk.MeetingService.Application.Interfaces;

public interface IMeetingChatNotifier
{
    Task BroadcastMessageReceivedAsync(Guid roomId, MeetingChatMessageDto message, CancellationToken ct = default);
    Task BroadcastMessageHiddenAsync(Guid roomId, Guid messageId, CancellationToken ct = default);
    Task BroadcastAssistantResponsePendingAsync(Guid roomId, Guid requestId, CancellationToken ct = default);

    /// <summary>
    /// Which tool WarpBot just reached for.
    ///
    /// The global assistant widget has shown this since it shipped; the in-meeting chat received
    /// the same event, used it only to flip a status, and threw the tool name away — so a
    /// meeting's WarpBot said "thinking" for the whole of a tool-calling loop and a slow answer
    /// was indistinguishable from a dead worker.
    /// </summary>
    /// <param name="toolDetail">
    /// What the call is about — the phrase searched, the file opened. Empty when there is no
    /// subject worth naming; the step still names its tool.
    /// </param>
    /// <summary>
    /// A piece of the answer as WarpBot writes it.
    ///
    /// The room used to see nothing at all between the question and the finished reply — the
    /// message is only persisted and broadcast once the whole turn is over, so a long answer
    /// looked like a stall while the widget beside it was visibly writing. The agent takes the
    /// same time on both surfaces; only one of them showed its work.
    ///
    /// `delta` is ADDITIVE: each one is the text since the last, exactly as the worker emits it,
    /// so a client concatenates rather than replaces. The persisted message that follows is
    /// authoritative — a client must swap the draft for it rather than keep its own accumulation.
    /// </summary>
    Task BroadcastAssistantChunkAsync(
        Guid roomId,
        Guid requestId,
        string delta,
        CancellationToken ct = default);

    Task BroadcastAssistantToolCallStartedAsync(
        Guid roomId, Guid requestId, string toolName, string toolDetail, CancellationToken ct = default);

    /// <summary>
    /// The model's own account of the step it is taking — a heading and the sentence under it.
    /// A tool step says what ran and never why, and between two calls there is no tool at all.
    /// </summary>
    Task BroadcastAssistantReasoningAsync(
        Guid roomId, Guid requestId, string title, string body, CancellationToken ct = default);
}
