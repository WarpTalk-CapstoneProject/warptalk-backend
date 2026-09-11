using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.Shared.Models;
using WarpTalk.Shared.Extensions;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.DTOs;

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
    /// Read this meeting's summary in a shape and language, generating that rendering if nobody
    /// has asked for it yet.
    ///
    /// A GET that can cause work, which is unusual enough to say why: the alternative is asking
    /// every client to POST a request, poll, and then GET — three calls to answer "show me this
    /// meeting in Japanese" — and the work is idempotent, cached, and bounded by the number of
    /// (shape, language) pairs a room's readers actually pick. It never modifies what any other
    /// reader sees, which is the property that separates it from the regenerate endpoint below.
    /// </summary>
    [HttpGet("rooms/{roomId}/summary")]
    public async Task<IActionResult> GetSummary(
        Guid roomId,
        [FromQuery] string? template = null,
        [FromQuery] string? language = null,
        CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        // Forwarded for the same reason the rewrite forwards it: if this request has to generate,
        // the worker reads the transcript as this caller and never through a privileged bypass.
        var bearerToken = Request.Headers["Authorization"].ToString();

        var result = await _artifactService.GetOrQueueSummaryVariantAsync(
            roomId, userId.Value, template ?? "general", language, bearerToken, ct);

        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound) return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Unauthorized) return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.InvalidState) return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
            return StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        // 202 while it is being written, so a client can tell "come back in a moment" from
        // "here it is" without inspecting the body — and so a cache never stores a pending
        // answer as though it were the summary.
        return result.Value!.Status == SummaryVariantStatus.Generating
            ? Accepted(result.Value)
            : Ok(result.Value);
    }

    /// <summary>Which renderings this room already holds, so a picker can show which are instant.</summary>
    [HttpGet("rooms/{roomId}/summary/renderings")]
    public async Task<IActionResult> GetSummaryRenderings(Guid roomId, CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _artifactService.GetSummaryVariantsAsync(roomId, userId.Value, ct);

        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound) return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Unauthorized) return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            return StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode));
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
            request?.Language,
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
        //
        // WT-669 — the id goes with it. Everything after this point happens out of the caller's
        // sight, and until they had something to ask about, every way it could go wrong looked
        // to them like the button doing nothing. Empty when the request was redirected to
        // finalization, which is a different pipeline with nothing to poll.
        return Accepted(new { message = "Summary rewrite queued.", requestId = result.Value ?? string.Empty });
    }

    /// <summary>
    /// What became of one rewrite. Polled by the client that asked for it, which is already
    /// refetching this room on a timer while it waits.
    /// </summary>
    [HttpGet("rooms/{roomId}/summary/regenerate/{requestId}")]
    public async Task<IActionResult> GetSummaryRewriteStatus(
        Guid roomId,
        string requestId,
        CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _artifactService.GetSummaryRewriteStatusAsync(roomId, userId.Value, requestId, ct);

        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.NotFound) return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.Unauthorized) return StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode));
            if (result.ErrorCode == ErrorCodes.InvalidState) return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
            return StatusCode(500, new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return Ok(result.Value);
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
/// <summary>
/// What a rewrite is allowed to change: the shape, the language, or both.
///
/// <c>Language</c> is optional and stays optional. A client that omits it is saying "leave the
/// language alone", which the AI side reads as "follow the transcript" — the behaviour every
/// summary written before this field existed was produced under, so an older client keeps
/// working unchanged rather than silently starting to translate.
/// </summary>
public record RegenerateSummaryRequest(string TemplateKey, string? Language = null);
