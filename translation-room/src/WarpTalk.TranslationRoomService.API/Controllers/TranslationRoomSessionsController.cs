using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Controllers;

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
        var result = await _sessionService.StartSessionAsync(roomId, dto, ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return BadRequest(new { Error = result.Error, Code = result.ErrorCode });
    }

    [HttpGet]
    public async Task<IActionResult> GetSessions(Guid roomId, CancellationToken ct)
    {
        var result = await _sessionService.GetSessionsAsync(roomId, ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return BadRequest(new { Error = result.Error, Code = result.ErrorCode });
    }

    [HttpPatch("{sessionId:guid}")]
    public async Task<IActionResult> UpdateSession(
        [FromRoute] Guid roomId,
        [FromRoute] Guid sessionId,
        [FromBody] UpdateTranslationRoomSessionDto dto,
        CancellationToken ct)
    {
        var result = await _sessionService.UpdateSessionAsync(roomId, sessionId, dto, ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return BadRequest(new { Error = result.Error, Code = result.ErrorCode });
    }

    [HttpPost("{sessionId:guid}/end")]
    public async Task<IActionResult> EndSession(
        [FromRoute] Guid roomId,
        [FromRoute] Guid sessionId,
        CancellationToken ct)
    {
        var result = await _sessionService.EndSessionAsync(roomId, sessionId, ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return BadRequest(new { Error = result.Error, Code = result.ErrorCode });
    }
}
