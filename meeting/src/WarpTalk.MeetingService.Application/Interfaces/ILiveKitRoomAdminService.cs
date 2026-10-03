using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Interfaces;

public interface ILiveKitRoomAdminService
{
    Task<Result<bool>> RemoveParticipantAsync(
        string roomName,
        string participantIdentity,
        CancellationToken ct = default);

    /// <summary>
    /// Silences a participant's microphone at the SFU, not in their browser.
    /// A "please mute yourself" message over the data channel is a request a modified or
    /// simply unresponsive client can ignore; this stops the track being forwarded at all,
    /// which is what a host asking for silence actually means.
    /// </summary>
    Task<Result<bool>> MuteParticipantMicrophoneAsync(
        string roomName,
        string participantIdentity,
        CancellationToken ct = default);

    Task<Result<bool>> DeleteRoomAsync(
        string roomName,
        CancellationToken ct = default);

    /// <summary>
    /// Who LiveKit says is in the room RIGHT NOW (RoomService.ListParticipants) — every
    /// participant, bots and recorders included; the caller decides who counts. A room LiveKit
    /// no longer has is an empty list, not a failure: "nobody is there" is the true answer.
    /// </summary>
    Task<Result<IReadOnlyList<LiveKitRoomParticipant>>> ListParticipantsAsync(
        string roomName,
        CancellationToken ct = default);
}

/// <summary>
/// One entry of LiveKit's ListParticipants. <see cref="Kind"/> and <see cref="State"/> are the
/// proto enum NAMES (STANDARD/INGRESS/EGRESS/SIP/AGENT, JOINING/JOINED/ACTIVE/DISCONNECTED) —
/// normalised from the ordinal when LiveKit sends a number, null when it sent nothing (proto3
/// omits the zero value, so a missing kind is STANDARD and a missing state is JOINING).
/// </summary>
public sealed record LiveKitRoomParticipant(string Identity, string? Kind, string? State);
