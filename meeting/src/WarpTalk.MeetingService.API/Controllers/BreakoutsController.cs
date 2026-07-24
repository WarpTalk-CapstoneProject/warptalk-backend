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
[Route("api/v1/meetings/rooms/{translationRoomId}/breakouts")]
[Authorize]
public class BreakoutsController : ControllerBase
{
    private readonly IBreakoutsService _breakoutsService;

    public BreakoutsController(IBreakoutsService breakoutsService)
    {
        _breakoutsService = breakoutsService;
    }

    [HttpPost]
    public async Task<IActionResult> Start(Guid translationRoomId, [FromBody] CreateBreakoutsRequest request)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));

        var result = await _breakoutsService.StartBreakoutsAsync(translationRoomId, userId.Value, request);
        return ToActionResult(result);
    }

    [HttpPost("end")]
    public async Task<IActionResult> End(Guid translationRoomId)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));

        var result = await _breakoutsService.EndBreakoutsAsync(translationRoomId, userId.Value);
        return ToActionResult(result);
    }

    /// <summary>Called by a client that just learned (via the BreakoutsStarted hub broadcast)
    /// that it has an assignment — mints a fresh LiveKit token for its own sub-room. Not part
    /// of the broadcast itself; see BreakoutAssignmentRelayDto's doc for why.</summary>
    [HttpGet("my-assignment")]
    public async Task<IActionResult> MyAssignment(Guid translationRoomId)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));

        var result = await _breakoutsService.GetMyAssignmentAsync(translationRoomId, userId.Value);
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
