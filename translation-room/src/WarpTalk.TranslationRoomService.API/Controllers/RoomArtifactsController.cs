using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.Shared.Models;
using WarpTalk.Shared.Extensions;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Controllers;

[ApiController]
[Route("api/v1/room-artifacts")]
[Authorize]
public class RoomArtifactsController : ControllerBase
{
    private readonly ITranslationRoomArtifactService _artifactService;

    public RoomArtifactsController(ITranslationRoomArtifactService artifactService)
    {
        _artifactService = artifactService;
    }

    [HttpGet("{id}/download")]
    public async Task<IActionResult> DownloadArtifact(Guid id, CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _artifactService.GetArtifactDownloadAsync(id, userId.Value, ct);

        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound) return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Unauthorized) return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Rewrite this meeting's summary in a different shape. The default is General, written
    /// once when the meeting ends; this is the second look, after somebody has read it.
    /// </summary>
    [HttpPost("rooms/{roomId}/summary/regenerate")]
    public async Task<IActionResult> RegenerateSummary(
        Guid roomId,
        [FromBody] RegenerateSummaryRequest request,
        CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        // Forwarded verbatim so the AI worker reads the transcript as this caller, through
        // the endpoint they could already use themselves.
        var bearerToken = Request.Headers["Authorization"].ToString();

        var result = await _artifactService.RegenerateSummaryAsync(
            roomId,
            userId.Value,
            request?.TemplateKey ?? "general",
            bearerToken,
            ct);

        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound) return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Unauthorized) return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.InvalidState) return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
            return StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        // Accepted, not Ok: the summary is not rewritten yet. It arrives over the artifacts
        // the client refetches, and saying "done" here would be a lie the UI then has to
        // work around.
        return Accepted(new { message = "Summary rewrite queued." });
    }

    [HttpPost("{id}/consent")]
    public async Task<IActionResult> ApproveConsent(Guid id, CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _artifactService.ApproveArtifactConsentAsync(id, userId.Value, ct);

        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound) return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Unauthorized) return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return NoContent();
    }
}

/// <summary>Which shape to rewrite the summary in. Unknown keys fall back to General.</summary>
public record RegenerateSummaryRequest(string TemplateKey);
