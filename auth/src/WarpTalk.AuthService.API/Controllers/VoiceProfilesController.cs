using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.AuthService.API.Controllers;

[ApiController]
[Route("api/v1/auth/voice-profiles")]
public class VoiceProfilesController : ControllerBase
{
    private readonly IVoiceProfileService _voiceProfileService;

    public VoiceProfilesController(IVoiceProfileService voiceProfileService)
    {
        _voiceProfileService = voiceProfileService;
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> GetProfiles(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _voiceProfileService.GetProfilesAsync(userId.Value, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [Authorize]
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<IActionResult> CreateProfile([FromForm] CreateVoiceProfileRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _voiceProfileService.CreateProfileAsync(userId.Value, request, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// Voices selectable for a language, from the provider's public library. Same source the
    /// in-meeting picker uses (TranslationRoomHub.GetVoiceCatalog), so both offer the same
    /// list. An empty list means the catalog has not been warmed for that language yet.
    /// </summary>
    [Authorize]
    [HttpGet("catalog")]
    public async Task<IActionResult> GetCatalog([FromQuery] string language, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _voiceProfileService.GetCatalogAsync(language, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// Pick the library voice this user hears for one language, or clear it by sending a
    /// null/empty voiceId. Returns the stored profile, or 204 when the preference was cleared.
    /// </summary>
    [Authorize]
    [HttpPut("preferred-voice")]
    public async Task<IActionResult> SetPreferredVoice([FromBody] SetPreferredVoiceRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _voiceProfileService.SetPreferredVoiceAsync(userId.Value, request, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }

        return result.Value == null ? NoContent() : Ok(result.Value);
    }

    /// <summary>
    /// Hear a voice speaking one sentence, before a meeting rather than during one.
    ///
    /// POST rather than GET because the first call for a voice does real work on the AI side —
    /// it is not a cached read that happens to be slow. Later calls for the same
    /// (voice, language) are served from that render.
    ///
    /// The container is WAV because that is what CartesiaSynthesizer.synthesize asks the
    /// provider for; it is fixed there, not negotiated here.
    /// </summary>
    [Authorize]
    [HttpPost("preview")]
    public async Task<IActionResult> PreviewVoice([FromBody] PreviewVoiceRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _voiceProfileService.PreviewVoiceAsync(userId.Value, request, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return File(result.Value!, "audio/wav");
    }

    [Authorize]
    [HttpDelete("{profileId:guid}")]
    public async Task<IActionResult> DeleteProfile(Guid profileId, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _voiceProfileService.DeleteProfileAsync(userId.Value, profileId, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return NoContent();
    }

    /// <summary>
    /// WT-396 — the voice this user is DUBBED IN.
    ///
    /// A separate route from the preferred-voice one, deliberately, because they point in
    /// opposite directions: that one is the voice you HEAR other people in, this one is how you
    /// sound to them. Sharing an endpoint is how an uploaded recording ended up changing neither.
    /// </summary>
    [Authorize]
    [HttpGet("dub-voice")]
    public async Task<IActionResult> GetDubVoice(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _voiceProfileService.GetDubVoiceAsync(userId.Value, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return Ok(new { voiceId = result.Value });
    }

    /// <summary>Pick, or clear with an empty voiceId, the voice this user is dubbed in.</summary>
    [Authorize]
    [HttpPut("dub-voice")]
    public async Task<IActionResult> SetDubVoice([FromBody] SetDubVoiceRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _voiceProfileService.SetDubVoiceAsync(userId.Value, request, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return Ok(new { voiceId = result.Value });
    }
}
