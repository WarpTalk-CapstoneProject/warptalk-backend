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

    [HttpPost("rooms/{translationRoomId}/participants/{participantId}/reject")]
    public async Task<IActionResult> RejectParticipant(Guid translationRoomId, Guid participantId)
    {
        var hostUserId = User.GetUserId();
        if (hostUserId == null)
        {
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));
        }

        var result = await _meetingRoomService.RejectParticipantAsync(translationRoomId, hostUserId.Value, participantId);
        
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            
            if (result.ErrorCode == ErrorCodes.Forbidden)
                return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));

            return StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(new { message = "Participant rejected from lobby" });
    }

    [HttpPost("rooms/{translationRoomId}/transfer-host/{newHostUserId}")]
    public async Task<IActionResult> TransferHost(Guid translationRoomId, Guid newHostUserId)
    {
        var currentHostUserId = User.GetUserId();
        if (currentHostUserId == null)
        {
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));
        }

        var result = await _meetingRoomService.TransferHostAsync(translationRoomId, currentHostUserId.Value, newHostUserId);
        
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            
            if (result.ErrorCode == ErrorCodes.Forbidden)
                return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));

            if (result.ErrorCode == ErrorCodes.BadRequest)
                return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));

            return StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(new { message = "Host role transferred successfully" });
    }

    [HttpPost("rooms/{translationRoomId}/participants/{participantId}/kick")]
    public async Task<IActionResult> KickParticipant(Guid translationRoomId, Guid participantId)
    {
        var hostUserId = User.GetUserId();
        if (hostUserId == null)
        {
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));
        }

        var result = await _meetingRoomService.KickParticipantAsync(translationRoomId, hostUserId.Value, participantId);
        
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            
            if (result.ErrorCode == ErrorCodes.Forbidden)
                return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));

            return StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(new { message = "Participant kicked and invitation revoked" });
    }

    [HttpPost("rooms/{translationRoomId}/end")]
    public async Task<IActionResult> EndMeeting(Guid translationRoomId)
    {
        var hostUserId = User.GetUserId();
        if (hostUserId == null)
        {
            return Unauthorized(new ApiErrorResponse("Invalid or missing user identity.", ErrorCodes.Unauthorized));
        }

        var result = await _meetingRoomService.EndMeetingAsync(translationRoomId, hostUserId.Value);
        
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound)
                return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            
            if (result.ErrorCode == ErrorCodes.Forbidden)
                return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));

            return StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(new { message = "Meeting ended successfully for all participants" });
    }
}
