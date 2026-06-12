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
}
