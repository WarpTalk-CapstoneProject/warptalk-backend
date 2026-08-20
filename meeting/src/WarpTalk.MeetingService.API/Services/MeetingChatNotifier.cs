using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using WarpTalk.MeetingService.API.Hubs;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.MeetingService.Application.Interfaces;

namespace WarpTalk.MeetingService.API.Services;

public class MeetingChatNotifier : IMeetingChatNotifier
{
    private readonly IHubContext<MeetingChatHub> _hubContext;

    public MeetingChatNotifier(IHubContext<MeetingChatHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task BroadcastMessageReceivedAsync(Guid roomId, MeetingChatMessageDto message, CancellationToken ct = default)
    {
        var groupName = MeetingChatHub.GetRoomGroupName(roomId);
        await _hubContext.Clients.Group(groupName).SendAsync("ChatMessageReceived", message, cancellationToken: ct);
    }

    public async Task BroadcastMessageHiddenAsync(Guid roomId, Guid messageId, CancellationToken ct = default)
    {
        var groupName = MeetingChatHub.GetRoomGroupName(roomId);
        await _hubContext.Clients.Group(groupName).SendAsync("ChatMessageHidden", messageId, cancellationToken: ct);
    }

    public async Task BroadcastAssistantResponsePendingAsync(Guid roomId, Guid requestId, CancellationToken ct = default)
    {
        var groupName = MeetingChatHub.GetRoomGroupName(roomId);
        await _hubContext.Clients.Group(groupName).SendAsync(
            "ChatAssistantResponsePending",
            new { requestId },
            cancellationToken: ct);
    }

    public async Task BroadcastAssistantToolCallStartedAsync(
        Guid roomId, Guid requestId, string toolName, CancellationToken ct = default)
    {
        var groupName = MeetingChatHub.GetRoomGroupName(roomId);
        await _hubContext.Clients.Group(groupName).SendAsync(
            "ChatAssistantToolCallStarted",
            new { requestId, toolName },
            cancellationToken: ct);
    }
}
