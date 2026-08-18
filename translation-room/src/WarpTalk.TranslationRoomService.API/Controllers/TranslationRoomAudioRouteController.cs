using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.TranslationRoomService.API.Controllers;

[ApiController]
[Route("api/v1/translation-rooms/{roomId:guid}/audio-routes")]
[Authorize]
public class TranslationRoomAudioRouteController : ControllerBase
{
    private readonly ITranslationRoomAudioRouteService _audioRouteService;

    public TranslationRoomAudioRouteController(ITranslationRoomAudioRouteService audioRouteService)
    {
        _audioRouteService = audioRouteService;
    }

    [HttpPost("generate")]
    public async Task<IActionResult> GenerateRoutes(Guid roomId, CancellationToken ct)
    {
        var result = await _audioRouteService.GenerateRoutesAsync(roomId, ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return BadRequest(new { Error = result.Error, Code = result.ErrorCode });
    }

    [HttpGet]
    public async Task<IActionResult> GetRoutes(Guid roomId, CancellationToken ct)
    {
        var result = await _audioRouteService.GetRoutesAsync(roomId, ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return BadRequest(new { Error = result.Error, Code = result.ErrorCode });
    }

    [HttpPatch("{routeId:guid}/runtime")]
    public async Task<IActionResult> UpdateRuntimeContext(
        [FromRoute] Guid roomId,
        [FromRoute] Guid routeId,
        [FromBody] UpdateAudioRouteRuntimeContextDto dto,
        CancellationToken ct)
    {
        var result = await _audioRouteService.UpdateRuntimeContextAsync(roomId, routeId, dto, ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return BadRequest(new { Error = result.Error, Code = result.ErrorCode });
    }

    [HttpPatch("{routeId:guid}/voice-clone")]
    public async Task<IActionResult> ToggleVoiceClone(
        [FromRoute] Guid roomId,
        [FromRoute] Guid routeId,
        [FromBody] ToggleVoiceCloneDto dto,
        CancellationToken ct)
    {
        var result = await _audioRouteService.ToggleVoiceCloneAsync(roomId, routeId, dto, ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return BadRequest(new { Error = result.Error, Code = result.ErrorCode });
    }

    /// <summary>
    /// Self-service: the calling participant has just changed the voice they are DUBBED IN (in
    /// AuthService, which owns that setting) and wants it to take effect in this meeting now.
    ///
    /// Carries no voice id on purpose — see ITranslationRoomAudioRouteService.RefreshDubVoiceAsync.
    /// This endpoint only says "go and re-read it".
    /// </summary>
    [HttpPost("dub-voice/refresh")]
    public async Task<IActionResult> RefreshDubVoice(
        [FromRoute] Guid roomId,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        var result = await _audioRouteService.RefreshDubVoiceAsync(roomId, userId.Value, ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return BadRequest(new { Error = result.Error, Code = result.ErrorCode });
    }

    /// <summary>
    /// Self-service: the calling participant consents (or withdraws consent) to have
    /// THEIR OWN voice cloned for every listener they currently speak to in this room.
    /// See ITranslationRoomAudioRouteService.SetVoiceCloneConsentAsync.
    /// </summary>
    [HttpPost("voice-clone-consent")]
    public async Task<IActionResult> SetVoiceCloneConsent(
        [FromRoute] Guid roomId,
        [FromBody] SetVoiceCloneConsentDto dto,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        var result = await _audioRouteService.SetVoiceCloneConsentAsync(roomId, userId.Value, dto.Enabled, ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return BadRequest(new { Error = result.Error, Code = result.ErrorCode });
    }
}
