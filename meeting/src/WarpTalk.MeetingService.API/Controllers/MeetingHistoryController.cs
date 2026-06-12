using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.MeetingService.API.Controllers;

[ApiController]
[Route("api/v1/meetings/history")]
[Authorize]
public class MeetingHistoryController : ControllerBase
{
    private readonly IMeetingHistoryService _historyService;

    public MeetingHistoryController(IMeetingHistoryService historyService)
    {
        _historyService = historyService;
    }

    /// <summary>
    /// Get paginated meeting history for the current user.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetMeetingHistory([FromQuery] GetMeetingHistoryRequest request, CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));

        var result = await _historyService.GetMeetingHistoryAsync(userId.Value, request, ct);

        if (!result.IsSuccess)
            return StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode));

        return Ok(result.Value);
    }

    /// <summary>
    /// Get detailed view of a specific meeting room (participants, chat summary).
    /// </summary>
    [HttpGet("{roomId}")]
    public async Task<IActionResult> GetMeetingRoomDetail(Guid roomId, CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));

        var result = await _historyService.GetMeetingRoomDetailAsync(roomId, userId.Value, ct);

        if (!result.IsSuccess)
        {
            if (result.ErrorCode == "NOT_FOUND")
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == "FORBIDDEN")
                return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            return StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value);
    }
}
