using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Interfaces;

public interface IMeetingRoomService
{
    Task<Result<JoinMeetingResponse>> JoinMeetingAsync(Guid translationRoomId, Guid userId);
    Task<Result<bool>> TriggerAiAsync(Guid translationRoomId, TriggerAiRequest request);
}
