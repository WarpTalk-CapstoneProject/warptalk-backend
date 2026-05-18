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
[Route("api/v1/meetings")]
[Authorize]
public class MeetingsController : ControllerBase
{
    private readonly IMeetingRoomService _meetingRoomService;

    public MeetingsController(IMeetingRoomService meetingRoomService)
    {
        _meetingRoomService = meetingRoomService;
    }

    [HttpPost("rooms/{translationRoomId}/join")]
    public async Task<IActionResult> JoinMeeting(Guid translationRoomId)
    {
        var userId = User.GetUserId();
        if (userId == null)
        {
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));
        }

        var result = await _meetingRoomService.JoinMeetingAsync(translationRoomId, userId.Value);
        
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            
            if (result.ErrorCode == ErrorCodes.Forbidden)
                return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));

            return StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value);
    }

    [HttpPost("rooms/{translationRoomId}/trigger-ai")]
    public async Task<IActionResult> TriggerAi(Guid translationRoomId, [FromBody] TriggerAiRequest req)
    {
        var result = await _meetingRoomService.TriggerAiAsync(translationRoomId, req);
        
        if (!result.IsSuccess)
        {
            return StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(new { message = "AI Triggered" });
    }
}
