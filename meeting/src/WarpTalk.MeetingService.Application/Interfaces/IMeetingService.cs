using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Interfaces;

public interface IMeetingService
{
    Task<Result<MeetingDto>> CreateMeetingAsync(CreateMeetingRequest request, Guid hostId, CancellationToken ct = default);
    Task<Result<MeetingDto>> GetMeetingAsync(Guid meetingId, CancellationToken ct = default);
    Task<Result<MeetingParticipantDto>> JoinMeetingAsync(Guid meetingId, Guid userId, JoinMeetingRequest request, CancellationToken ct = default);
    Task<Result> EndMeetingAsync(Guid meetingId, Guid hostId, CancellationToken ct = default);
}
