using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Controllers;

/// <summary>
/// [Authorize] was the ONLY thing guarding this controller: it proved the caller held a valid
/// token and nothing else. No action read <c>User.GetUserId()</c>, and the service behind it never
/// took a caller identity, so any authenticated user could POST a session, PATCH its status or
/// audio URL, or mark it ENDED on any room id — including rooms in workspaces they have never
/// belonged to. Every action now resolves the caller and passes it down; the predicates are the
/// ones the rest of the service already uses (see ITranslationRoomSessionService).
///
/// Forbidden is surfaced as 403 rather than folded into the 400 every failure used to return, so a
/// refusal is distinguishable from a validation error by clients and in logs.
/// </summary>
[ApiController]
[Route("api/v1/translation-rooms/{roomId:guid}/sessions")]
[Authorize]
public class TranslationRoomSessionsController : ControllerBase
{
    private readonly ITranslationRoomSessionService _sessionService;

    public TranslationRoomSessionsController(ITranslationRoomSessionService sessionService)
    {
        _sessionService = sessionService;
    }

    [HttpPost]
    public async Task<IActionResult> StartSession(Guid roomId, [FromBody] CreateTranslationRoomSessionDto dto, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _sessionService.StartSessionAsync(roomId, dto, userId.Value, ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return Failure(result.Error, result.ErrorCode);
    }

    [HttpGet]
    public async Task<IActionResult> GetSessions(Guid roomId, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _sessionService.GetSessionsAsync(roomId, userId.Value, User.GetEmail(), ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return Failure(result.Error, result.ErrorCode);
    }

    [HttpPatch("{sessionId:guid}")]
    public async Task<IActionResult> UpdateSession(
        [FromRoute] Guid roomId,
        [FromRoute] Guid sessionId,
        [FromBody] UpdateTranslationRoomSessionDto dto,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _sessionService.UpdateSessionAsync(roomId, sessionId, dto, userId.Value, ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return Failure(result.Error, result.ErrorCode);
    }

    [HttpPost("{sessionId:guid}/end")]
    public async Task<IActionResult> EndSession(
        [FromRoute] Guid roomId,
        [FromRoute] Guid sessionId,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _sessionService.EndSessionAsync(roomId, sessionId, userId.Value, ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return Failure(result.Error, result.ErrorCode);
    }

    /// <summary>
    /// Preserves the pre-existing <c>{ Error, Code }</c> body every caller of this controller
    /// already parses — only the status code for Forbidden/NotFound is new.
    /// </summary>
    private IActionResult Failure(string? error, string? code)
    {
        if (code == ErrorCodes.Forbidden) return StatusCode(403, new { Error = error, Code = code });
        if (code == ErrorCodes.NotFound) return NotFound(new { Error = error, Code = code });
        return BadRequest(new { Error = error, Code = code });
    }
}
