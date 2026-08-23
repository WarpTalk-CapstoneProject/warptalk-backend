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
    /// <summary>
    /// The meeting room has not been provisioned yet — the caller should retry. Distinct from
    /// <see cref="NotAParticipant"/>, which no amount of retrying will change.
    /// </summary>
    public const string RoomNotReady = "Room not ready";

    /// <summary>The caller does not belong to this meeting. Terminal; retrying is pointless.</summary>
    public const string NotAParticipant = "Not a participant";

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

        // NOT AN ERROR ABOUT THE ROOM — a race with the meeting join.
        //
        // MeetingRoomService.JoinMeetingAsync is what provisions this row, and the chat hub
        // connects alongside that call rather than after it. So on a first entry the row
        // routinely does not exist yet for a second or two. The old message said "Room not
        // found", which reads as a permanent, fatal answer; the client gave up after ~5s of
        // retries and silently had no live chat for the rest of the meeting.
        //
        // Named distinctly so the client can tell "come back in a moment" apart from "you may
        // not be here", and retry only the first.
        if (room == null) throw new HubException(RoomNotReady);

        // AUTHORIZATION, RATHER THAN MANUFACTURING THE MEMBERSHIP THAT AUTHORIZES.
        //
        // This used to create a MeetingParticipant row for whoever asked, with no check at all,
        // and then add them to the group. MeetingChatService gates reading and sending on
        // `room.CreatedBy == userId || participant != null` — so the hub was minting exactly the
        // record those gates look for. Any authenticated user who knew a translation room id
        // could call this and thereby become a participant of a meeting they were never invited
        // to, which also let them read its history over REST.
        //
        // Membership belongs to the join, which enforces invitations, revocation, expiry and the
        // room lock (MeetingRoomService.JoinMeetingAsync, step 3, before it registers anyone).
        // The hub only subscribes a connection to a room it can already see the caller belongs to,
        // and mutates nothing.
        var participant = await _unitOfWork.MeetingParticipantRepository
            .FirstOrDefaultAsync(p => p.MeetingRoomId == room.Id && p.UserId == userId);

        if (room.CreatedBy != userId && participant == null)
            throw new HubException(NotAParticipant);

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
