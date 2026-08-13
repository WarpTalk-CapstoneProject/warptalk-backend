using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;
using WarpTalk.AuthService.API.Common;

namespace WarpTalk.AuthService.API.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class GoogleAuthController : ControllerBase
{
    private readonly IGoogleAuthService _googleAuthService;

    public GoogleAuthController(IGoogleAuthService googleAuthService)
    {
        _googleAuthService = googleAuthService;
    }

    [HttpPost("google-login")]
    public async Task<IActionResult> GoogleLogin([FromBody] GoogleLoginRequest request, CancellationToken ct)
    {
        var loginRequest = request with
        {
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            DeviceInfo = Request.Headers.UserAgent.ToString()
        };
        var result = await _googleAuthService.GoogleLoginAsync(loginRequest, ct);
        if (!result.IsSuccess)
        {
            // WT-361 — the same lesson TokenController.Refresh already learned in WT-344: a
            // failure to ANSWER is not a verdict on the credential.
            //
            // Every failure here returned 400, so a database blip, an unreachable Google, or a
            // misconfigured client id were all indistinguishable from a token we looked at and
            // refused. The bug report for this endpoint is literally "400 Bad Request" and
            // nothing more, because 400 is the only thing it can say.
            //
            // 5xx is the honest answer for "we could not check", and it also tells the browser
            // this is worth retrying — which a rejected token never is.
            var isServiceFault =
                result.ErrorCode == ErrorCodes.InternalServerError
                || result.ErrorCode == ErrorCodes.ServiceUnavailable;

            if (isServiceFault)
            {
                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    new ApiErrorResponse(result.Error, result.ErrorCode));
            }

            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        var auth = result.Value!;
        AuthSessionCookies.Write(Request, Response, auth);
        return Ok(AuthSessionCookies.ToResponse(auth));
    }

    [Authorize]
    [HttpPost("google/link")]
    public async Task<IActionResult> LinkGoogle([FromBody] LinkGoogleRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _googleAuthService.LinkGoogleAsync(userId.Value, request, ct);
        if (!result.IsSuccess)
        {
            var errorResponse = new ApiErrorResponse(result.Error, result.ErrorCode);
            if (result.ErrorCode == ErrorCodes.Forbidden) return StatusCode(403, errorResponse);
            return BadRequest(errorResponse);
        }
        return NoContent();
    }

    [Authorize]
    [HttpPost("google/unlink")]
    public async Task<IActionResult> UnlinkGoogle(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _googleAuthService.UnlinkGoogleAsync(userId.Value, ct);
        if (!result.IsSuccess)
        {
            var errorResponse = new ApiErrorResponse(result.Error, result.ErrorCode);
            if (result.ErrorCode == ErrorCodes.Forbidden) return StatusCode(403, errorResponse);
            return BadRequest(errorResponse);
        }
        return NoContent();
    }
}
