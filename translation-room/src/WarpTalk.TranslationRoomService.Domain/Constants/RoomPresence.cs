using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Domain.Constants;

/// <summary>
/// "Is anybody actually in this room?" — the question both abandoned-room reapers ask before they
/// end a meeting.
///
/// WHY THIS IS NOT <see cref="TranslationRoomParticipantStatuses.HoldsSeat"/>
///     A seat and a person are the same thing in every room except one. An EXTERNAL_BRIDGE room
///     seeds a stand-in for the far side of the Google Meet call at creation, CONNECTED, and it
///     must hold a seat: the audio mesh is built from seat holders, and without it a bridge room
///     generates no routes at all (see TranslationRoomMapper.BuildExternalBridgeParticipant).
///
///     But nothing ever releases that seat short of End. MarkParticipantDisconnectedAsync runs off
///     the hub's OnDisconnectedAsync and the stand-in has no hub connection. So counted as a
///     person, it made every bridge room look occupied forever: a host who pressed Leave, or whose
///     app crashed, left a room IN_PROGRESS with nobody in it, which never reached History, never
///     finalized its artifacts, and could never have minutes drafted.
///
///     Capacity and the mesh still read seats; only "should this room be ended" reads this.
/// </summary>
public static class RoomPresence
{
    /// <summary>
    /// The far-side stand-in of an EXTERNAL_BRIDGE room. Recognised by its fixed user id — the
    /// one identity three services already agree on (WarpTalk.Shared.ExternalBridgeConstants) —
    /// rather than by ConnectionType, so the in-memory and SQL forms of this rule can be the same
    /// comparison.
    /// </summary>
    public static bool IsExternalBridgeStandIn(TranslationRoomParticipant participant) =>
        participant.UserId == TranslationRoomConstants.ExternalBridgeParticipantUserId;

    /// <summary>True when <paramref name="participant"/> is a real person currently in the room.</summary>
    public static bool IsPersonInRoom(TranslationRoomParticipant participant) =>
        TranslationRoomParticipantStatuses.HoldsSeat(participant.Status) &&
        !IsExternalBridgeStandIn(participant);
}
