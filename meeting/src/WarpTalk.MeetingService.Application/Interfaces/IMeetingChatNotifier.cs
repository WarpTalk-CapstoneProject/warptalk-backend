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
    Task BroadcastAssistantToolCallStartedAsync(
        Guid roomId, Guid requestId, string toolName, string toolDetail, CancellationToken ct = default);
}
