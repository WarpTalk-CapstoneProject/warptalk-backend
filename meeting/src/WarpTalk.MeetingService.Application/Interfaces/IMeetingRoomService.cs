using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Interfaces;

public interface IMeetingRoomService
{
    Task<Result<JoinMeetingResponse>> JoinMeetingAsync(Guid translationRoomId, Guid userId, string? displayName = null);
    Task<Result<bool>> TriggerAiAsync(Guid translationRoomId, TriggerAiRequest request);

    // WT-525: a LiveKit token for the EXTERNAL_BRIDGE stand-in seat. The ONLY method here that
    // mints a token for an identity other than the caller's, which is why it takes the caller
    // separately and refuses unless they host the room AND the room is a bridge.
    Task<Result<BridgeTokenResponse>> GenerateBridgeTokenAsync(Guid translationRoomId, Guid callerUserId);
    Task<Result<bool>> RejectParticipantAsync(Guid translationRoomId, Guid hostUserId, Guid participantUserId);
    Task<Result<bool>> TransferHostAsync(Guid translationRoomId, Guid currentHostUserId, Guid newHostUserId);
    Task<Result<bool>> KickParticipantAsync(Guid translationRoomId, Guid hostUserId, Guid participantUserId);
    Task<Result<bool>> MuteParticipantAsync(Guid translationRoomId, Guid hostUserId, Guid participantUserId);
    Task<Result<bool>> EndMeetingAsync(Guid translationRoomId, Guid hostUserId);

    // WT-04: host controls.
    Task<Result<bool>> SetLockAsync(Guid translationRoomId, Guid callerUserId, bool locked);
    Task<Result<bool>> SetMuteOnEntryAsync(Guid translationRoomId, Guid callerUserId, bool muteOnEntry);

    // WT-06: recording via LiveKit Egress.
    Task<Result<RecordingStateDto>> SetRecordingAsync(Guid translationRoomId, Guid callerUserId, string action);

    // WT-08: authoritative host-fallback election, triggered by the Gateway hub's
    // OnDisconnectedAsync (via the "translationRoom:participant-offline" Redis pub/sub
    // event it already publishes unconditionally on a full disconnect). Idempotent —
    // safe to call even when the departed user was not the host, or when a host has
    // already been elected by a previous invocation.
    Task<Result<bool>> HandleHostOfflineAsync(Guid translationRoomId, Guid departedUserId);
}
