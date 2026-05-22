using System;
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

    public TranscriptsController(ITranscriptQueryService transcriptQueryService)
    {
        _transcriptQueryService = transcriptQueryService;
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
