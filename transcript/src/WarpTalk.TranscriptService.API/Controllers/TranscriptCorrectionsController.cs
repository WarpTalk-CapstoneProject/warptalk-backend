using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Interfaces;

namespace WarpTalk.TranscriptService.API.Controllers;

[Authorize]
[ApiController]
[Route("api/segments/{segmentId}/corrections")]
public class TranscriptCorrectionsController : ControllerBase
{
    private readonly ITranscriptCorrectionService _correctionService;

    public TranscriptCorrectionsController(ITranscriptCorrectionService correctionService)
    {
        _correctionService = correctionService;
    }

    [HttpPost]
    public async Task<ActionResult> SubmitCorrection(
        Guid segmentId,
        [FromBody] CreateCorrectionDto request,
        CancellationToken cancellationToken)
    {
        var result = await _correctionService.SubmitCorrectionAsync(segmentId, request, cancellationToken);
        
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
    public async Task<ActionResult<IEnumerable<TranscriptCorrectionDto>>> GetCorrections(
        Guid segmentId,
        CancellationToken cancellationToken)
    {
        var result = await _correctionService.GetCorrectionsBySegmentIdAsync(segmentId, cancellationToken);
        
        if (!result.IsSuccess)
        {
            return StatusCode(500, result.Error);
        }

        return Ok(result.Value);
    }
}
