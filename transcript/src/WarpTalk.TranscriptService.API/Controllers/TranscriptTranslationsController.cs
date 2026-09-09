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
    private readonly ITranscriptTranslationBackfillService _backfillService;

    public TranscriptTranslationsController(
        ITranscriptQueryService transcriptQueryService,
        ITranscriptTranslationBackfillService backfillService)
    {
        _transcriptQueryService = transcriptQueryService;
        _backfillService = backfillService;
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

    /// <summary>
    /// How much of this transcript can be read in one language, and whether a backfill is running.
    /// Poll this to follow one; it is cheap and the counts are the progress.
    /// </summary>
    [HttpGet("coverage")]
    public async Task<ActionResult<TranscriptLanguageCoverageDto>> GetCoverage(
        Guid transcriptId,
        [FromQuery] string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdString, out var userId))
            return Unauthorized();

        var result = await _backfillService.GetCoverageAsync(transcriptId, userId, targetLanguage, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : Translate(result.ErrorCode, result.Error);
    }

    /// <summary>
    /// Translates the lines this transcript has no <c>targetLanguage</c> version of, so that
    /// choosing a language means reading the whole meeting in it rather than the part of it that
    /// happened to be translated while the meeting was running.
    ///
    /// Returns 202 with the coverage as it stands: the work is done by warptalk-ai and lands over
    /// Redis, so the counts move under the caller and are meant to be polled from
    /// <c>GET .../translations/coverage</c>.
    /// </summary>
    [HttpPost("backfill")]
    public async Task<ActionResult<TranscriptLanguageCoverageDto>> Backfill(
        Guid transcriptId,
        [FromBody] BackfillTranscriptLanguageRequest request,
        CancellationToken cancellationToken = default)
    {
        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdString, out var userId))
            return Unauthorized();

        var result = await _backfillService.RequestBackfillAsync(
            transcriptId, userId, request?.TargetLanguage ?? string.Empty, cancellationToken);

        if (!result.IsSuccess)
        {
            return Translate(result.ErrorCode, result.Error);
        }

        return Accepted(result.Value);
    }

    private ActionResult Translate(string? errorCode, string? error) =>
        errorCode switch
        {
            "NOT_FOUND" => NotFound(new { Message = error }),
            "FORBIDDEN" => Forbid(),
            "VALIDATION_ERROR" => BadRequest(new { Message = error }),
            "TOO_LARGE" => BadRequest(new { Message = error }),
            _ => StatusCode(500, new { Message = error })
        };
}
