using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;
using WarpTalk.Shared.Models;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Controllers;

/// <summary>
/// WT-327: the recurring BOOKING, not its meetings.
///
/// Creating one goes through POST /api/v1/translation-rooms with a `recurrence` block — the
/// client asks for a meeting and gets one back, whether or not it repeats. This controller only
/// exists for the two things that are about the series itself: reading its rule, and stopping it.
/// </summary>
[ApiController]
[Route("api/v1/translation-room-series")]
[Authorize]
public class TranslationRoomSeriesController : ControllerBase
{
    private readonly ITranslationRoomSeriesService _seriesService;

    public TranslationRoomSeriesController(ITranslationRoomSeriesService seriesService)
    {
        _seriesService = seriesService;
    }

    /// <summary>
    /// The booking, its rule, and the occurrences this caller may see.
    ///
    /// Always NotFound on a refusal — the service returns the same not-found Result for "no such
    /// series" and "not yours" deliberately, so this mapping stays one branch and cannot grow a
    /// 403 that re-confirms the id to whoever guessed it.
    /// </summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetSeries(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _seriesService.GetSeriesAsync(id, userId.Value, User.GetEmail(), ct);
        if (!result.IsSuccess)
            return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));

        return Ok(result.Value!);
    }

    /// <summary>
    /// Edits the booking and every occurrence still ahead of it.
    ///
    /// Cancelling ONE occurrence remains the per-room cancel
    /// (POST /api/v1/translation-rooms/{id}/cancel): it skips a single day, the watermark keeps
    /// the sweep from regenerating it, and the series carries on.
    /// </summary>
    [HttpPatch("{id}")]
    public async Task<IActionResult> UpdateSeries(Guid id, [FromBody] UpdateSeriesRequest request, CancellationToken ct)
    {
        var hostId = User.GetUserId();
        if (hostId == null) return Unauthorized();

        var result = await _seriesService.UpdateSeriesAsync(id, hostId.Value, request, ct);
        if (!result.IsSuccess)
        {
            return result.ErrorCode switch
            {
                ErrorCodes.NotFound => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
                ErrorCodes.Forbidden => StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode)),
                ErrorCodes.InvalidState => Conflict(new ApiErrorResponse(result.Error, result.ErrorCode)),
                _ => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            };
        }

        return Ok(result.Value!);
    }

    /// <summary>
    /// Stops the series and cancels its FUTURE occurrences.
    ///
    /// Cancelling ONE occurrence is the existing per-room cancel
    /// (POST /api/v1/translation-rooms/{id}/cancel) and is unaffected: it skips a single day and
    /// the series keeps going. "This and all following" is deliberately NOT implemented — see
    /// the PR body — rather than half-implemented behind an ambiguous button.
    /// </summary>
    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> CancelSeries(
        Guid id,
        // WT-548: the occurrence the host is looking at while they press the button. It is KEPT.
        // Optional so an older client, or a caller with no occurrence in hand, behaves exactly as
        // before — but the meeting page always sends it, because "stop repeating" cancelling the
        // meeting you are standing on is the bug this parameter exists to fix.
        [FromQuery] Guid? keep,
        CancellationToken ct)
    {
        var hostId = User.GetUserId();
        if (hostId == null) return Unauthorized();

        var result = await _seriesService.CancelSeriesAsync(id, hostId.Value, keep, ct);
        if (!result.IsSuccess)
        {
            return result.ErrorCode switch
            {
                ErrorCodes.NotFound => NotFound(new ApiErrorResponse(result.Error, result.ErrorCode)),
                ErrorCodes.Forbidden => StatusCode(403, new ApiErrorResponse(result.Error, result.ErrorCode)),
                _ => BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode)),
            };
        }

        return Ok(result.Value!);
    }
}
