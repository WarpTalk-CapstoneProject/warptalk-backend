using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class MeetingsController : ControllerBase
{
    private readonly IMeetingService _meetingService;

    public MeetingsController(IMeetingService meetingService)
    {
        _meetingService = meetingService;
    }

    [HttpPost]
    public async Task<IActionResult> CreateMeeting([FromBody] CreateMeetingRequest request, CancellationToken ct)
    {
        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out var hostId))
            return Unauthorized();

        var result = await _meetingService.CreateMeetingAsync(request, hostId, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return Ok(result.Value);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetMeeting(Guid id, CancellationToken ct)
    {
        var result = await _meetingService.GetMeetingAsync(id, ct);
        if (!result.IsSuccess)
            return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));

        return Ok(result.Value);
    }

    [HttpPost("{id}/join")]
    public async Task<IActionResult> JoinMeeting(Guid id, [FromBody] JoinMeetingRequest request, CancellationToken ct)
    {
        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out var userId))
            return Unauthorized();

        var result = await _meetingService.JoinMeetingAsync(id, userId, request, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return Ok(result.Value);
    }

    [HttpPost("{id}/end")]
    public async Task<IActionResult> EndMeeting(Guid id, CancellationToken ct)
    {
        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out var hostId))
            return Unauthorized();

        var result = await _meetingService.EndMeetingAsync(id, hostId, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

        return NoContent();
    }
}
