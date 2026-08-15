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
public class TokenController : ControllerBase
{
    private readonly ITokenService _tokenService;

    public TokenController(ITokenService tokenService)
    {
        _tokenService = tokenService;
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequest request, CancellationToken ct)
    {
        var refreshToken = string.IsNullOrWhiteSpace(request.RefreshToken)
            ? Request.Cookies[AuthSessionCookies.RefreshCookieName]
            : request.RefreshToken;
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return BadRequest(new ApiErrorResponse(
                "Refresh token is required.",
                ErrorCodes.ValidationError));
        }

        var refreshRequest = request with
        {
            RefreshToken = refreshToken,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            DeviceInfo = Request.Headers.UserAgent.ToString()
        };
        var result = await _tokenService.RefreshTokenAsync(refreshRequest, ct);
        if (!result.IsSuccess)
        {
            // WT-344 — a failure to ANSWER is not a verdict on the token.
            //
            // Every failure used to return 400 and clear the browser's session cookies. That is
            // correct for a token this service looked at and refused, and catastrophic for the
            // other kind: RefreshTokenAsync's catch-all returns InternalServerError when the
            // database is unreachable, so a few seconds of DB unavailability during a deploy told
            // every open browser "your refresh token is invalid" — and the web client, quite
            // reasonably, reads any 4xx from this endpoint as a dead session and signs the user
            // out. Users were being logged out by our own rolling deploys, ~60 seconds in.
            //
            // 5xx is the honest answer for "we could not check". The client already treats 5xx
            // and network failures as transient and keeps the session, so this one status change
            // is what stops a blip from ending a week-long session.
            var isServiceFault =
                result.ErrorCode == ErrorCodes.InternalServerError
                || result.ErrorCode == ErrorCodes.ServiceUnavailable;

            if (isServiceFault)
            {
                // Emphatically NOT clearing the cookies. They are the only copy of a refresh
                // token that is, as far as anyone knows, still perfectly good — deleting it over
                // our own outage turns a retryable moment into a mandatory re-login.
                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    new ApiErrorResponse(result.Error, result.ErrorCode));
            }

            AuthSessionCookies.Clear(Request, Response);
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        var auth = result.Value!;
        AuthSessionCookies.Write(Request, Response, auth);
        return Ok(AuthSessionCookies.ToResponse(auth));
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var refreshToken = string.IsNullOrWhiteSpace(request.RefreshToken)
            ? Request.Cookies[AuthSessionCookies.RefreshCookieName]
            : request.RefreshToken;
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            AuthSessionCookies.Clear(Request, Response);
            return NoContent();
        }

        var result = await _tokenService.LogoutAsync(userId.Value, refreshToken, ct);

        // Before the status is decided, and on every path out of here.
        //
        // WT-405: this used to sit after the failure check, so a logout the service could not
        // complete returned 400 with all three cookies still in the jar. The browser has already
        // torn its own session down by the time this request is sent — it does not, and cannot,
        // put the session back — so refusing to clear leaves the two sides disagreeing: signed
        // out in the tab, signed in here, with a session marker that still says "there is a
        // refresh token worth redeeming".
        //
        // Clearing a cookie is not the same act as revoking a token, and only one of them can
        // fail. Whether the family was revoked is what the status code is for; the cookies name
        // a session this browser has finished with either way, and holding on to them protects
        // nothing.
        AuthSessionCookies.Clear(Request, Response);

        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return NoContent();
    }
}
