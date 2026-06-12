using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System;
using System.Security.Claims;
using System.Threading.Tasks;

namespace WarpTalk.MeetingService.API.Hubs;

[Authorize]
public class MeetingChatHub : Hub
{
    // Clients connect to /api/v1/meetings/chat-hub
    // Then they join a room group to receive messages
    
    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
    }

    public async Task JoinMeetingChat(Guid roomId)
    {
        var groupName = GetRoomGroupName(roomId);
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
    }

    public async Task LeaveMeetingChat(Guid roomId)
    {
        var groupName = GetRoomGroupName(roomId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
    }

    public static string GetRoomGroupName(Guid roomId) => $"meeting_chat:{roomId}";
}
