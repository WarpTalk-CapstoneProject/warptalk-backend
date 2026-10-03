using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;
using WarpTalk.Shared.Models;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Constants;

namespace WarpTalk.TranslationRoomService.API.Controllers;

/// <summary>
/// One shared EXTERNAL_BRIDGE room per Google Meet code, with a single audio capturer. See
/// TranslationRoomService.Bridge for the rules.
/// </summary>
[ApiController]
[Route("api/v1/translation-rooms")]
[Authorize]
public class BridgeRoomsController : ControllerBase
{
    private readonly ITranslationRoomService _translationRoomService;
    private readonly IBridgeVoiceCloneConsentService _voiceCloneConsents;

    public BridgeRoomsController(
        ITranslationRoomService translationRoomService,
        IBridgeVoiceCloneConsentService voiceCloneConsents)
    {
        _translationRoomService = translationRoomService;
        _voiceCloneConsents = voiceCloneConsents;
    }

    /// <summary>
    /// Find-or-create this Meet call's room in the workspace, join it, and learn whether this
    /// desktop captures the far side (<c>bridgeRole: "capturer"</c>) or only publishes its mic
    /// (<c>"member"</c>). 200 whether the room was created or reused (<c>created</c> says which).
    /// </summary>
    [HttpPost("bridge/claim")]
    public async Task<IActionResult> Claim([FromBody] ClaimBridgeRoomRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        // Creating a room requires a verified email on POST /translation-rooms; claim may create.
        if (!User.IsEmailVerified())
            return StatusCode(403, new ApiErrorResponse("Email not verified", ErrorCodes.AccountPending));

        var result = await _translationRoomService.ClaimBridgeRoomAsync(
            request, userId.Value, User.FindFirstValue(ClaimTypes.Email), ct);

        return result.IsSuccess ? Ok(result.Value) : Error(result.Error, result.ErrorCode);
    }

    /// <summary>The capturer renews its lease. 409 when the caller is not (or no longer) the capturer.</summary>
    [HttpPost("{id:guid}/bridge/capturer/heartbeat")]
    public async Task<IActionResult> Heartbeat(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _translationRoomService.HeartbeatBridgeCapturerAsync(id, userId.Value, ct);
        return result.IsSuccess ? Ok(result.Value) : Error(result.Error, result.ErrorCode);
    }

    /// <summary>
    /// A participant takes over capturing when there is no capturer or its lease is stale.
    /// 409 while a live capturer holds it.
    /// </summary>
    [HttpPost("{id:guid}/bridge/capturer/takeover")]
    public async Task<IActionResult> TakeOver(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _translationRoomService.TakeOverBridgeCapturerAsync(id, userId.Value, ct);
        return result.IsSuccess ? Ok(result.Value) : Error(result.Error, result.ErrorCode);
    }

    /// <summary>
    /// Sets the caller's OWN bridge audio mode: <c>{ "mode": "text" }</c> when they are in Meet with
    /// their real mic and speakers (no dub of their speech is synthesized for the far side),
    /// <c>"voice"</c> when a virtual cable carries their dub. Voice → text is always allowed;
    /// text → voice answers 409 <c>BRIDGE_AUDIO_MODE_LOCKED</c> while translation is running.
    /// 400 bad mode, 403 not a participant, 404 no room, 409 INVALID_STATE not a bridge / ended.
    /// </summary>
    [HttpPut("{id:guid}/bridge/audio-mode")]
    public async Task<IActionResult> SetAudioMode(Guid id, [FromBody] SetBridgeAudioModeRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _translationRoomService.SetBridgeAudioModeAsync(id, userId.Value, request?.Mode, ct);
        return result.IsSuccess ? Ok(result.Value) : Error(result.Error, result.ErrorCode);
    }

    /// <summary>
    /// WT-933. The HOST records that one Meet-side person agreed to have their voice cloned
    /// (<c>{ "displayName": "...", "consented": true }</c>) or withdraws it (<c>false</c>).
    /// Idempotent both ways; answers the request echoed. The name is hashed before it goes
    /// anywhere — it is never stored and never logged.
    /// 400 blank name or longer than 100 chars, 403 not the host, 404 no room, 409 INVALID_STATE
    /// not a bridge / ended.
    /// </summary>
    [HttpPut("{id:guid}/bridge/voice-clone-consents")]
    public async Task<IActionResult> SetVoiceCloneConsent(
        Guid id, [FromBody] SetBridgeVoiceCloneConsentRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _voiceCloneConsents.SetAsync(
            id, userId.Value, request?.DisplayName, request?.Consented ?? false, ct);
        return result.IsSuccess ? Ok(result.Value) : Error(result.Error, result.ErrorCode);
    }

    /// <summary>
    /// WT-933. Which of these Meet display names have a consent in force:
    /// <c>{ "displayNames": [...] }</c> (at most 50) → <c>{ "consented": [...] }</c>, the subset
    /// exactly as submitted. A POST with a body on purpose — names must not travel in a URL.
    /// Host only; same refusals as the PUT, and 400 for a missing list or more than 50 names.
    /// </summary>
    [HttpPost("{id:guid}/bridge/voice-clone-consents/status")]
    public async Task<IActionResult> GetVoiceCloneConsentStatus(
        Guid id, [FromBody] BridgeVoiceCloneConsentStatusRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _voiceCloneConsents.GetStatusAsync(id, userId.Value, request?.DisplayNames, ct);
        return result.IsSuccess ? Ok(result.Value) : Error(result.Error, result.ErrorCode);
    }

    /// <summary>
    /// ApiErrorStatus, except INVALID_STATE (not a bridge / room already ended), which these
    /// endpoints report as 409: the request is well-formed, the room is just past the point where it
    /// can be captured.
    /// </summary>
    private ObjectResult Error(string? error, string? errorCode)
    {
        var status = errorCode is ErrorCodes.InvalidState or BridgeRoomConstants.ErrorCodeAudioModeLocked
            ? 409
            : ApiErrorStatus.For(errorCode);
        return StatusCode(status, new ApiErrorResponse(error, errorCode));
    }
}
