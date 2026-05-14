using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Interfaces;

using Microsoft.AspNetCore.Authorization;

namespace WarpTalk.TranscriptService.API.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/transcripts/{transcriptId}/segments")]
public class TranscriptSegmentsController : ControllerBase
{
    private readonly ITranscriptQueryService _transcriptQueryService;

    public TranscriptSegmentsController(ITranscriptQueryService transcriptQueryService)
    {
        _transcriptQueryService = transcriptQueryService;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<TranscriptSegmentDto>>> GetSegments(
        Guid transcriptId, 
        [FromQuery] int skip = 0, 
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await _transcriptQueryService.GetSegmentsAsync(transcriptId, skip, take, cancellationToken);
        
        if (!result.IsSuccess)
        {
            return result.ErrorCode switch
            {
                "NOT_FOUND" => NotFound(new { Message = result.Error }),
                _ => StatusCode(500, new { Message = result.Error })
            };
        }

        return Ok(result.Value);
    }
}
