using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.AuthService.API.Controllers;

/// <summary>
/// A person's own consent to voice cloning. Every route acts on the CALLER — there is no user id
/// in any path here, and that is the point: nobody grants this on somebody else's behalf, not an
/// admin and not a workspace owner. The biometric is the person's.
/// </summary>
[ApiController]
[Route("api/v1/auth/voice-consent")]
public class VoiceConsentController : ControllerBase
{
    private readonly IVoiceConsentService _voiceConsentService;

    public VoiceConsentController(IVoiceConsentService voiceConsentService)
    {
        _voiceConsentService = voiceConsentService;
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _voiceConsentService.GetStatusAsync(userId.Value, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [Authorize]
    [HttpPost("grant")]
    public async Task<IActionResult> Grant(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _voiceConsentService.GrantAsync(userId.Value, DecisionContext(), ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [Authorize]
    [HttpPost("revoke")]
    public async Task<IActionResult> Revoke(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _voiceConsentService.RevokeAsync(userId.Value, DecisionContext(), ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// The evidence stored with a decision. X-Forwarded-For is read first because every request
    /// reaches this service through the gateway, so RemoteIpAddress is the gateway on every call
    /// and would make the record say the same thing for everybody. Only the first hop is kept —
    /// the rest of that header is proxies, not people.
    /// </summary>
    private VoiceConsentDecisionContext DecisionContext()
    {
        var forwarded = Request.Headers["X-Forwarded-For"].ToString();
        var ip = string.IsNullOrWhiteSpace(forwarded)
            ? HttpContext.Connection.RemoteIpAddress?.ToString()
            : forwarded.Split(',')[0].Trim();

        return new VoiceConsentDecisionContext(ip, Request.Headers.UserAgent.ToString());
    }
}
