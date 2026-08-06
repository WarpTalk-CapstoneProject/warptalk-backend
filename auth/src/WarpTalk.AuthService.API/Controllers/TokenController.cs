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
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        AuthSessionCookies.Clear(Request, Response);
        return NoContent();
    }
}
