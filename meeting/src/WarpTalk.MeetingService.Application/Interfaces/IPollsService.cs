using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Interfaces;

public interface IPollsService
{
    Task<Result<PollDto>> CreatePollAsync(Guid translationRoomId, Guid callerUserId, CreatePollRequest request, CancellationToken ct = default);
    Task<Result<PollDto>> VoteAsync(Guid translationRoomId, Guid pollId, Guid callerUserId, VotePollRequest request, CancellationToken ct = default);
    Task<Result<PollDto>> CloseAsync(Guid translationRoomId, Guid pollId, Guid callerUserId, CancellationToken ct = default);
    Task<Result<List<PollDto>>> ListAsync(Guid translationRoomId, Guid callerUserId, CancellationToken ct = default);
}
