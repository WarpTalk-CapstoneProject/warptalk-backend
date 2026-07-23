using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.MeetingService.API.Controllers;

[ApiController]
[Route("api/v1/meetings/rooms/{translationRoomId}/questions")]
[Authorize]
public class QuestionsController : ControllerBase
{
    private readonly IQuestionsService _questionsService;

    public QuestionsController(IQuestionsService questionsService)
    {
        _questionsService = questionsService;
    }

    [HttpPost]
    public async Task<IActionResult> Ask(Guid translationRoomId, [FromBody] CreateQuestionRequest request)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));

        var result = await _questionsService.AskAsync(translationRoomId, userId.Value, request);
        return ToActionResult(result);
    }

    [HttpPost("{questionId}/upvote")]
    public async Task<IActionResult> Upvote(Guid translationRoomId, Guid questionId)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));

        var result = await _questionsService.UpvoteAsync(translationRoomId, questionId, userId.Value);
        return ToActionResult(result);
    }

    [HttpPost("{questionId}/answer")]
    public async Task<IActionResult> Answer(Guid translationRoomId, Guid questionId)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));

        var result = await _questionsService.AnswerAsync(translationRoomId, questionId, userId.Value);
        return ToActionResult(result);
    }

    [HttpGet]
    public async Task<IActionResult> List(Guid translationRoomId)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));

        var result = await _questionsService.ListAsync(translationRoomId, userId.Value);
        return ToActionResult(result);
    }

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess)
            return Ok(result.Value);

        return result.ErrorCode switch
        {
            ErrorCodes.NotFound => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Forbidden => StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.ValidationError => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.InvalidState => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            _ => StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode)),
        };
    }
}
