using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;

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
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return Ok(result.Value);
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
