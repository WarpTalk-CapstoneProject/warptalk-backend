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
[Route("api/v1/meetings/rooms/{translationRoomId}/polls")]
[Authorize]
public class PollsController : ControllerBase
{
    private readonly IPollsService _pollsService;

    public PollsController(IPollsService pollsService)
    {
        _pollsService = pollsService;
    }

    [HttpPost]
    public async Task<IActionResult> CreatePoll(Guid translationRoomId, [FromBody] CreatePollRequest request)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));

        var result = await _pollsService.CreatePollAsync(translationRoomId, userId.Value, request);
        return ToActionResult(result);
    }

    [HttpPost("{pollId}/vote")]
    public async Task<IActionResult> Vote(Guid translationRoomId, Guid pollId, [FromBody] VotePollRequest request)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));

        var result = await _pollsService.VoteAsync(translationRoomId, pollId, userId.Value, request);
        return ToActionResult(result);
    }

    [HttpPost("{pollId}/close")]
    public async Task<IActionResult> Close(Guid translationRoomId, Guid pollId)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));

        var result = await _pollsService.CloseAsync(translationRoomId, pollId, userId.Value);
        return ToActionResult(result);
    }

    [HttpGet]
    public async Task<IActionResult> List(Guid translationRoomId)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));

        var result = await _pollsService.ListAsync(translationRoomId, userId.Value);
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
