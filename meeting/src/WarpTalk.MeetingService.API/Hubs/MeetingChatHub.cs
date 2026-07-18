using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System;
using System.Security.Claims;
using System.Threading.Tasks;
using WarpTalk.MeetingService.Domain.Interfaces;

namespace WarpTalk.MeetingService.API.Hubs;

[Authorize]
public class MeetingChatHub : Hub
{
    private readonly IUnitOfWork _unitOfWork;

    public MeetingChatHub(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
    }

    public async Task JoinMeetingRoom(Guid roomId)
    {
        var userIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out var userId))
            throw new HubException("Unauthorized");

        var room = await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.TranslationRoomId == roomId);
        if (room == null) throw new HubException("Room not found");

        var participant = await _unitOfWork.MeetingParticipantRepository
            .FirstOrDefaultAsync(p => p.MeetingRoomId == room.Id && p.UserId == userId);

        if (participant == null || !participant.IsActive)
            throw new HubException("Forbidden: You are not an active participant in this room.");

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
