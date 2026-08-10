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
}
