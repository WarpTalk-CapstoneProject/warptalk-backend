using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Interfaces;

namespace WarpTalk.TranscriptService.API.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/transcripts")]
public class TranscriptsController : ControllerBase
{
    private readonly ITranscriptQueryService _transcriptQueryService;
    private readonly ITranscriptCorrectionService _transcriptCorrectionService;
    private readonly ITranscriptRecordingService _transcriptRecordingService;

    public TranscriptsController(
        ITranscriptQueryService transcriptQueryService,
        ITranscriptCorrectionService transcriptCorrectionService,
        ITranscriptRecordingService transcriptRecordingService)
    {
        _transcriptQueryService = transcriptQueryService;
        _transcriptCorrectionService = transcriptCorrectionService;
        _transcriptRecordingService = transcriptRecordingService;
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<TranscriptDto>> GetTranscript(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var result = await _transcriptQueryService.GetTranscriptAsync(id, userId, cancellationToken);
        return ToActionResult(result);
    }

    [HttpGet("by-room/{translationRoomId}")]
    public async Task<ActionResult<TranscriptDto>> GetTranscriptByTranslationRoom(Guid translationRoomId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var result = await _transcriptQueryService.GetTranscriptByTranslationRoomAsync(translationRoomId, userId, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("{id}/finalize")]
    public async Task<IActionResult> FinalizeTranscript(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var result = await _transcriptCorrectionService.FinalizeTranscriptAsync(
            id,
            userId,
            cancellationToken);
        if (result.IsSuccess)
            return NoContent();

        return result.ErrorCode switch
        {
            "NOT_FOUND" => NotFound(new { Message = result.Error }),
            "UNAUTHORIZED" => StatusCode(403, new { Message = result.Error }),
            "BAD_REQUEST" => BadRequest(new { Message = result.Error }),
            _ => StatusCode(500, new { Message = result.Error })
        };
    }

    /// <summary>
    /// WT-605. Stop the transcript from being written down; translation, dubbing, subtitles and
    /// LiveKit keep running untouched. Host-only. See TranscriptRecordingService for why this is
    /// not the same switch as Pause Room / Stop Translation.
    /// </summary>
    [HttpPost("by-room/{translationRoomId}/pause")]
    public async Task<IActionResult> PauseTranscript(Guid translationRoomId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var result = await _transcriptRecordingService.PauseAsync(translationRoomId, userId, cancellationToken);
        if (result.IsSuccess)
            return NoContent();

        return result.ErrorCode switch
        {
            "FORBIDDEN" => StatusCode(403, new { Message = result.Error }),
            "INVALID_STATE" => Conflict(new { Message = result.Error }),
            _ => StatusCode(500, new { Message = result.Error })
        };
    }

    /// <summary>The counterpart to <see cref="PauseTranscript"/>. Host-only.</summary>
    [HttpPost("by-room/{translationRoomId}/resume")]
    public async Task<IActionResult> ResumeTranscript(Guid translationRoomId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var result = await _transcriptRecordingService.ResumeAsync(translationRoomId, userId, cancellationToken);
        if (result.IsSuccess)
            return NoContent();

        return result.ErrorCode switch
        {
            "FORBIDDEN" => StatusCode(403, new { Message = result.Error }),
            "INVALID_STATE" => Conflict(new { Message = result.Error }),
            _ => StatusCode(500, new { Message = result.Error })
        };
    }

    /// <summary>Every pause/resume window for this room's transcript, for the panel's dividers.
    /// Open to anyone who can read the transcript, not only the host.</summary>
    [HttpGet("by-room/{translationRoomId}/pause-windows")]
    public async Task<ActionResult<IReadOnlyList<TranscriptPauseWindowDto>>> GetTranscriptPauseWindows(Guid translationRoomId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var result = await _transcriptRecordingService.GetPauseWindowsAsync(translationRoomId, userId, cancellationToken);
        return ToActionResult(result);
    }

    private bool TryGetUserId(out Guid userId)
    {
        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(userIdString, out userId);
    }

    private ActionResult<T> ToActionResult<T>(WarpTalk.Shared.Result<T> result)
    {
        if (result.IsSuccess)
            return Ok(result.Value);

        return result.ErrorCode switch
        {
            "NOT_FOUND" => NotFound(new { Message = result.Error }),
            "FORBIDDEN" => Forbid(),
            _ => StatusCode(500, new { Message = result.Error })
        };
    }
}
