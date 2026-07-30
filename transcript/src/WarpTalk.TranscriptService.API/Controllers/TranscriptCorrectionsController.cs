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
[Route("api/v1/transcripts/{transcriptId}/segments/{segmentId}")]
public class TranscriptCorrectionsController : ControllerBase
{
    private readonly ITranscriptCorrectionService _correctionService;

    public TranscriptCorrectionsController(ITranscriptCorrectionService correctionService)
    {
        _correctionService = correctionService;
    }

    [HttpPost]
    [Route("correct")]
    public async Task<ActionResult> SubmitCorrection(
        Guid transcriptId,
        Guid segmentId,
        [FromBody] CreateCorrectionDto request,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var result = await _correctionService.SubmitCorrectionAsync(transcriptId, segmentId, userId, request, cancellationToken);
        
        if (!result.IsSuccess)
        {
            return result.ErrorCode switch
            {
                "NOT_FOUND" => NotFound(result.Error),
                "BAD_REQUEST" => BadRequest(result.Error),
                "UNAUTHORIZED" => StatusCode(403, result.Error),
                _ => StatusCode(500, result.Error)
            };
        }

        return StatusCode(201);
    }

    [HttpGet]
    [Route("corrections")]
    public async Task<ActionResult<IEnumerable<TranscriptCorrectionDto>>> GetCorrections(
        Guid transcriptId,
        Guid segmentId,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized();

        var result = await _correctionService.GetCorrectionsBySegmentIdAsync(transcriptId, segmentId, userId, cancellationToken);
        
        if (!result.IsSuccess)
        {
            return result.ErrorCode switch
            {
                "NOT_FOUND" => NotFound(result.Error),
                "UNAUTHORIZED" => StatusCode(403, result.Error),
                _ => StatusCode(500, result.Error)
            };
        }

        return Ok(result.Value);
    }

    private bool TryGetUserId(out Guid userId)
    {
        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(userIdString, out userId);
    }
}
