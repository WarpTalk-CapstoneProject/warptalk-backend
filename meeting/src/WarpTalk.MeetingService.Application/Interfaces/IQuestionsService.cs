using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Interfaces;

public interface IQuestionsService
{
    Task<Result<QuestionDto>> AskAsync(Guid translationRoomId, Guid callerUserId, CreateQuestionRequest request, CancellationToken ct = default);
    Task<Result<QuestionDto>> UpvoteAsync(Guid translationRoomId, Guid questionId, Guid callerUserId, CancellationToken ct = default);
    Task<Result<QuestionDto>> AnswerAsync(Guid translationRoomId, Guid questionId, Guid callerUserId, CancellationToken ct = default);
    Task<Result<List<QuestionDto>>> ListAsync(Guid translationRoomId, Guid callerUserId, CancellationToken ct = default);
}
