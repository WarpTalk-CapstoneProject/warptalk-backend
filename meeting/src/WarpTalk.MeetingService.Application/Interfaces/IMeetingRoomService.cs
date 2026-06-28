using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Interfaces;

public interface IMeetingRoomService
{
    Task<Result<JoinMeetingResponse>> JoinMeetingAsync(Guid translationRoomId, Guid userId, string? displayName = null);
    Task<Result<bool>> TriggerAiAsync(Guid translationRoomId, TriggerAiRequest request);
    Task<Result<bool>> RejectParticipantAsync(Guid translationRoomId, Guid hostUserId, Guid participantUserId);
    Task<Result<bool>> TransferHostAsync(Guid translationRoomId, Guid currentHostUserId, Guid newHostUserId);
    Task<Result<bool>> KickParticipantAsync(Guid translationRoomId, Guid hostUserId, Guid participantUserId);
    Task<Result<bool>> EndMeetingAsync(Guid translationRoomId, Guid hostUserId);
}
