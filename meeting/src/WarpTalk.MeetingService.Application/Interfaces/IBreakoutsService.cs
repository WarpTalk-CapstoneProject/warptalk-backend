using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Interfaces;

public interface IBreakoutsService
{
    Task<Result<CreateBreakoutsResponse>> StartBreakoutsAsync(Guid translationRoomId, Guid callerUserId, CreateBreakoutsRequest request, CancellationToken ct = default);
    Task<Result<bool>> EndBreakoutsAsync(Guid translationRoomId, Guid callerUserId, CancellationToken ct = default);
    Task<Result<BreakoutJoinInfoDto>> GetMyAssignmentAsync(Guid translationRoomId, Guid callerUserId, CancellationToken ct = default);
}
