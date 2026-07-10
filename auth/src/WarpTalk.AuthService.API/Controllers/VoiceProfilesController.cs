using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.AuthService.API.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/auth/voice-profiles")]
public class VoiceProfilesController : ControllerBase
{
    private readonly IVoiceProfileService _voiceProfileService;

    public VoiceProfilesController(IVoiceProfileService voiceProfileService)
    {
        _voiceProfileService = voiceProfileService;
    }

    [HttpGet]
    public async Task<IActionResult> GetProfiles([FromQuery] Guid? workspaceId, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _voiceProfileService.GetProfilesAsync(userId.Value, workspaceId, ct);
        return ToActionResult(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetProfile(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _voiceProfileService.GetProfileAsync(userId.Value, id, ct);
        return ToActionResult(result);
    }

    [HttpPost]
    public async Task<IActionResult> CreateProfile([FromBody] CreateVoiceProfileRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _voiceProfileService.CreateProfileAsync(userId.Value, request, ct);
        if (!result.IsSuccess) return ToActionResult(result);

        return CreatedAtAction(nameof(GetProfile), new { id = result.Value!.Id }, result.Value);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateProfile(Guid id, [FromBody] UpdateVoiceProfileRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _voiceProfileService.UpdateProfileAsync(userId.Value, id, request, ct);
        return ToActionResult(result);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteProfile(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _voiceProfileService.DeleteProfileAsync(userId.Value, id, ct);
        if (!result.IsSuccess)
        {
            return ToActionResult(result);
        }

        return NoContent();
    }

    [HttpPost("{id:guid}/samples")]
    public async Task<IActionResult> AddSample(Guid id, [FromBody] AddVoiceSampleRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _voiceProfileService.AddSampleAsync(userId.Value, id, request, ct);
        return ToActionResult(result);
    }

    [HttpPost("{id:guid}/consents")]
    public async Task<IActionResult> GrantConsent(Guid id, [FromBody] GrantVoiceConsentRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _voiceProfileService.GrantConsentAsync(
            userId.Value,
            id,
            request,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers["User-Agent"].ToString(),
            ct);

        return ToActionResult(result);
    }

    [HttpPost("{id:guid}/consents/revoke")]
    public async Task<IActionResult> RevokeConsent(Guid id, [FromBody] RevokeVoiceConsentRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _voiceProfileService.RevokeConsentAsync(
            userId.Value,
            id,
            request,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers["User-Agent"].ToString(),
            ct);

        return ToActionResult(result);
    }

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);

        var errorResponse = new ApiErrorResponse(result.Error, result.ErrorCode);
        return result.ErrorCode switch
        {
            ErrorCodes.NotFound or ErrorCodes.UserNotFound => NotFound(errorResponse),
            ErrorCodes.Unauthorized => Unauthorized(errorResponse),
            ErrorCodes.Forbidden => Forbid(),
            _ => BadRequest(errorResponse)
        };
    }

    private IActionResult ToActionResult(Result result)
    {
        var errorResponse = new ApiErrorResponse(result.Error, result.ErrorCode);
        return result.ErrorCode switch
        {
            ErrorCodes.NotFound or ErrorCodes.UserNotFound => NotFound(errorResponse),
            ErrorCodes.Unauthorized => Unauthorized(errorResponse),
            ErrorCodes.Forbidden => Forbid(),
            _ => BadRequest(errorResponse)
        };
    }
}
