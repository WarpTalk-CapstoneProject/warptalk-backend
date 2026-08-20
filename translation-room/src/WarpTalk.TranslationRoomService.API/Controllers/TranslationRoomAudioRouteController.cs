using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.TranslationRoomService.API.Controllers;

[ApiController]
[Route("api/v1/translation-rooms/{roomId:guid}/audio-routes")]
[Authorize]
public class TranslationRoomAudioRouteController : ControllerBase
{
    private readonly ITranslationRoomAudioRouteService _audioRouteService;
    private readonly IRoomFlashModeService _flashMode;
    private readonly IMicrophoneNoiseReductionService _noiseReduction;

    public TranslationRoomAudioRouteController(
        ITranslationRoomAudioRouteService audioRouteService,
        IRoomFlashModeService flashMode,
        IMicrophoneNoiseReductionService noiseReduction)
    {
        _audioRouteService = audioRouteService;
        _flashMode = flashMode;
        _noiseReduction = noiseReduction;
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

    /// <summary>
    /// Flash mode for this room — whether a speaker's audio is streamed to STT while they are
    /// still talking, instead of only once VAD closes the turn.
    ///
    /// Readable by any participant so a guest's UI can show what the host chose; writable by the
    /// host alone, because unlike voice-clone consent and the dub-voice refresh above it, this
    /// changes how EVERYBODY in the room is transcribed.
    ///
    /// It lives on this controller rather than on TranslationRoomsController because it is a
    /// property of the audio pipeline, and this is the client the meeting UI already talks to for
    /// the rest of it.
    /// </summary>
    [HttpGet("flash-mode")]
    public async Task<IActionResult> GetFlashMode([FromRoute] Guid roomId, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        var result = await _flashMode.GetAsync(roomId, userId.Value, ct);

        if (result.IsSuccess)
        {
            // Source travels with the value: "on because the host said so" and "on because that
            // is what this deployment does" are the same switch position and different sentences
            // underneath it, and the UI cannot write the right one without being told which.
            return Ok(new { result.Value!.Enabled, result.Value.Source });
        }

        return NotFound(new { Error = result.Error, Code = result.ErrorCode });
    }

    [HttpPut("flash-mode")]
    public async Task<IActionResult> SetFlashMode(
        [FromRoute] Guid roomId,
        [FromBody] SetFlashModeDto dto,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        var result = await _flashMode.SetAsync(roomId, userId.Value, dto.Enabled, ct);

        if (result.IsSuccess)
        {
            // Always "room": succeeding here IS the act of setting an override, whatever the
            // deployment default happens to be. Shaped like the GET so one client type covers both.
            return Ok(new { Enabled = result.Value, Source = FlashModeSources.Room });
        }

        // A non-host gets 403, not 400: the request was well formed and they are simply not
        // allowed, and a UI that renders the switch needs to tell those two apart.
        if (result.ErrorCode == ErrorCodes.Forbidden)
        {
            return StatusCode(403, new { Error = result.Error, Code = result.ErrorCode });
        }

        return BadRequest(new { Error = result.Error, Code = result.ErrorCode });
    }

    /// <summary>
    /// How much denoising the STT provider applies to THIS CALLER'S OWN microphone in this meeting.
    ///
    /// Self-service, unlike flash mode above it: this changes how one person's microphone is
    /// handled and touches nobody else's audio, so requiring the host would mean a guest in a noisy
    /// room has to ask permission to be understood. See IMicrophoneNoiseReductionService.
    ///
    /// The caller's own id is always the subject. There is no parameter for choosing whose
    /// microphone, deliberately.
    /// </summary>
    [HttpGet("noise-reduction")]
    public async Task<IActionResult> GetNoiseReduction(
        [FromRoute] Guid roomId,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        var result = await _noiseReduction.GetAsync(roomId, userId.Value, ct);

        if (result.IsSuccess)
        {
            return Ok(new { Mode = result.Value });
        }

        return NotFound(new { Error = result.Error, Code = result.ErrorCode });
    }

    [HttpPut("noise-reduction")]
    public async Task<IActionResult> SetNoiseReduction(
        [FromRoute] Guid roomId,
        [FromBody] SetNoiseReductionDto dto,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        var result = await _noiseReduction.SetAsync(roomId, userId.Value, dto.Mode, ct);

        if (result.IsSuccess)
        {
            return Ok(new { Mode = result.Value });
        }

        // A caller who is not in the room gets 404, an unusable mode gets 400. Collapsing the two
        // would leave a client unable to tell "you are not here" from "that is not a mode".
        if (result.ErrorCode == ErrorCodes.NotFound)
        {
            return NotFound(new { Error = result.Error, Code = result.ErrorCode });
        }

        return BadRequest(new { Error = result.Error, Code = result.ErrorCode });
    }
}
