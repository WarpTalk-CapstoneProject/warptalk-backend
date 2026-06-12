using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Interfaces;

public interface IMeetingHistoryService
{
    Task<Result<MeetingHistoryResponse>> GetMeetingHistoryAsync(Guid userId, GetMeetingHistoryRequest request, CancellationToken ct = default);
    Task<Result<MeetingRoomDetailDto>> GetMeetingRoomDetailAsync(Guid roomId, Guid userId, CancellationToken ct = default);
}
