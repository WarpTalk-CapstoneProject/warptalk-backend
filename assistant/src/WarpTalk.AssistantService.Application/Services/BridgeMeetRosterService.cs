using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Services;

/// <summary>
/// GET translation-rooms/{id}/bridge/meet-participants: who is in the Google Meet a bridge room
/// bridges, read through the HOST's Google grant, for the host's labelling UI.
/// </summary>
public interface IBridgeMeetRosterService
{
    Task<Result<IReadOnlyList<MeetParticipantDto>>> GetAsync(Guid roomId, Guid callerId, CancellationToken ct = default);
}

public sealed class BridgeMeetRosterService : IBridgeMeetRosterService
{
    public static class ErrorCodes
    {
        public const string RoomNotFound = "room_not_found";
        public const string Forbidden = "forbidden";
        public const string NotABridgeRoom = "not_a_bridge_room";
        public const string RoomUnavailable = "room_unavailable";
    }

    private readonly IBridgeRoomDirectory _rooms;
    private readonly IHostMeetConferenceService _meet;

    public BridgeMeetRosterService(IBridgeRoomDirectory rooms, IHostMeetConferenceService meet)
    {
        _rooms = rooms;
        _meet = meet;
    }

    public async Task<Result<IReadOnlyList<MeetParticipantDto>>> GetAsync(Guid roomId, Guid callerId, CancellationToken ct = default)
    {
        var (status, room) = await _rooms.GetAsync(roomId, ct);
        if (status == BridgeRoomLookupStatus.NotFound || (status == BridgeRoomLookupStatus.Found && room is null))
            return Result.Failure<IReadOnlyList<MeetParticipantDto>>("Room not found.", ErrorCodes.RoomNotFound);
        if (status == BridgeRoomLookupStatus.Unavailable)
            return Result.Failure<IReadOnlyList<MeetParticipantDto>>("The room could not be read.", ErrorCodes.RoomUnavailable);

        // The host and the room's own participants only. The roster names people in somebody
        // else's Google Meet; being able to guess a room id must not be enough to read it.
        var isHost = callerId == room!.EffectiveHostId || callerId == room.BookerId;
        if (!isHost && !room.MemberUserIds.Contains(callerId))
            return Result.Failure<IReadOnlyList<MeetParticipantDto>>("Only the room's host and participants can see this.", ErrorCodes.Forbidden);

        var host = room.EffectiveHostId ?? room.BookerId;
        if (!room.IsBridge || string.IsNullOrWhiteSpace(room.ExternalMeetingUrl) || host is null)
            return Result.Failure<IReadOnlyList<MeetParticipantDto>>("This room does not bridge a Google Meet.", ErrorCodes.NotABridgeRoom);

        // plugin_not_connected / connection_required / meet_scope_missing pass through as the
        // host's own answer: the roster is read with the HOST's grant, whoever asks.
        var roster = await _meet.GetRosterAsync(host.Value, room.WorkspaceId, room.ExternalMeetingUrl, ct);
        return roster.IsSuccess
            ? Result.Success(roster.Value!.Participants)
            : Result.Failure<IReadOnlyList<MeetParticipantDto>>(roster.Error!, roster.ErrorCode);
    }
}
