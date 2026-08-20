using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;
using WarpTalk.Shared.Models;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Controllers;

/// <summary>
/// Biên bản họp — the signed meeting record.
///
/// Separate from RoomArtifactsController because a minutes document is not an artifact: artifacts
/// are outputs a job produced, and this has a lifecycle, an owner and a signature.
/// </summary>
[ApiController]
[Route("api/v1/rooms/{roomId}/minutes")]
[Authorize]
public class MeetingMinutesController : ControllerBase
{
    private readonly IMeetingMinutesService _minutesService;

    public MeetingMinutesController(IMeetingMinutesService minutesService)
    {
        _minutesService = minutesService;
    }

    [HttpGet]
    public async Task<IActionResult> GetCurrent(Guid roomId, CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        return Respond(await _minutesService.GetCurrentAsync(roomId, userId.Value, User.GetEmail(), ct));
    }

    /// <summary>Draw up the draft. Idempotent while one is still unapproved.</summary>
    [HttpPost("draft")]
    public async Task<IActionResult> CreateDraft(Guid roomId, CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        return Respond(await _minutesService.CreateDraftAsync(roomId, userId.Value, ct));
    }

    [HttpPut("{minutesId}")]
    public async Task<IActionResult> UpdateContent(
        Guid roomId,
        Guid minutesId,
        [FromBody] UpdateMinutesContentRequest request,
        CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(request?.Content))
        {
            return BadRequest(new ApiErrorResponse("Minutes content is required.", ErrorCodes.ValidationError));
        }

        return Respond(await _minutesService.UpdateContentAsync(
            roomId, minutesId, userId.Value, request.Content, ct));
    }

    [HttpPost("{minutesId}/sign")]
    public async Task<IActionResult> Sign(Guid roomId, Guid minutesId, CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        return Respond(await _minutesService.SignAsync(roomId, minutesId, userId.Value, ct));
    }

    [HttpPost("{minutesId}/approve")]
    public async Task<IActionResult> Approve(Guid roomId, Guid minutesId, CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        return Respond(await _minutesService.ApproveAsync(roomId, minutesId, userId.Value, ct));
    }

    [HttpPost("{minutesId}/revise")]
    public async Task<IActionResult> Revise(Guid roomId, Guid minutesId, CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        return Respond(await _minutesService.ReviseAsync(roomId, minutesId, userId.Value, ct));
    }

    /// <summary>Download the minutes as a Word document.</summary>
    [HttpGet("export.docx")]
    public async Task<IActionResult> ExportDocx(Guid roomId, CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _minutesService.ExportDocxAsync(roomId, userId.Value, User.GetEmail(), ct);

        if (!result.IsSuccess)
        {
            return result.ErrorCode switch
            {
                ErrorCodes.NotFound => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
                ErrorCodes.Forbidden => StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode)),
                ErrorCodes.InvalidState => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
                _ => StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode))
            };
        }

        var file = result.Value!;
        return File(file.Bytes, file.ContentType, file.FileName);
    }

    private IActionResult Respond(Result<MeetingMinutesDto> result)
    {
        if (result.IsSuccess) return Ok(result.Value);

        return result.ErrorCode switch
        {
            ErrorCodes.NotFound => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Forbidden => StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Unauthorized => StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.Conflict => Conflict(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.InvalidState => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            ErrorCodes.ValidationError => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            _ => StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode))
        };
    }
}
