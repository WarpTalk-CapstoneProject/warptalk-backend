using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Interfaces;

namespace WarpTalk.TranscriptService.API.Controllers;

/// <summary>
/// WT-716 tier 2: the transcript read as whole cleaned sentences. Sits beside
/// <see cref="TranscriptSegmentsController"/> and answers exactly like it — same paging, same
/// status codes, and the same read gate through <see cref="ITranscriptQueryService"/> — because it
/// is the same text in a different shape.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/transcripts/{transcriptId}/clean-sentences")]
public class TranscriptCleanSentencesController : ControllerBase
{
    private readonly ITranscriptQueryService _transcriptQueryService;

    public TranscriptCleanSentencesController(ITranscriptQueryService transcriptQueryService)
    {
        _transcriptQueryService = transcriptQueryService;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<TranscriptCleanSentenceDto>>> GetCleanSentences(
        Guid transcriptId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdString, out var userId))
            return Unauthorized();

        var result = await _transcriptQueryService.GetCleanSentencesAsync(transcriptId, userId, skip, take, cancellationToken);

        if (!result.IsSuccess)
        {
            return result.ErrorCode switch
            {
                "NOT_FOUND" => NotFound(new { Message = result.Error }),
                "FORBIDDEN" => Forbid(),
                _ => StatusCode(500, new { Message = result.Error })
            };
        }

        return Ok(result.Value);
    }
}
