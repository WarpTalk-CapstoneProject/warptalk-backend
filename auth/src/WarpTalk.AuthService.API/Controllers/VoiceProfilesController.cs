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

        var result = await _voiceProfileService.CreateProfileAsync(
            userId.Value,
            request,
            ct,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers["User-Agent"].ToString());
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
}
