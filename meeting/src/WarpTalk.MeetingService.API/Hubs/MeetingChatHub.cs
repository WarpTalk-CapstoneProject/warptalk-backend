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
        var userIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdString))
        {
            throw new HubException("Unauthorized");
        }

        // Validate Participant is active
        var unitOfWork = Context.GetHttpContext()?.RequestServices.GetService(typeof(WarpTalk.MeetingService.Domain.Interfaces.IUnitOfWork)) as WarpTalk.MeetingService.Domain.Interfaces.IUnitOfWork;
        if (unitOfWork != null)
        {
            var participant = await unitOfWork.MeetingParticipantRepository
                .FirstOrDefaultAsync(p => p.MeetingRoomId == roomId && p.ProviderIdentity == userIdString);

            if (participant == null || !participant.IsActive)
            {
                throw new HubException("Forbidden: You are not an active participant in this room.");
            }
        }

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
