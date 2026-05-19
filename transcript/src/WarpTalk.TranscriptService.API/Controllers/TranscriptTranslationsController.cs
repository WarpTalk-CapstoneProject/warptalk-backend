using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Interfaces;

using Microsoft.AspNetCore.Authorization;

namespace WarpTalk.TranscriptService.API.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/transcripts/{transcriptId}/translations")]
public class TranscriptTranslationsController : ControllerBase
{
    private readonly ITranscriptQueryService _transcriptQueryService;

    public TranscriptTranslationsController(ITranscriptQueryService transcriptQueryService)
    {
        _transcriptQueryService = transcriptQueryService;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<TranscriptTranslationDto>>> GetTranslations(
        Guid transcriptId, 
        [FromQuery] int skip = 0, 
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdString, out var userId))
            return Unauthorized();

        var result = await _transcriptQueryService.GetTranslationsAsync(transcriptId, userId, skip, take, cancellationToken);
        
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
