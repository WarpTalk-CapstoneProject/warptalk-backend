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
}
