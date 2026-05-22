using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.AuthService.API.Helpers;

namespace WarpTalk.AuthService.API.Controllers;

[ApiController]
[Route("api/auth")]
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
        var refreshRequest = request with
        {
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            DeviceInfo = Request.Headers.UserAgent.ToString()
        };

        var result = await _tokenService.RefreshTokenAsync(refreshRequest, ct);
        if (!result.IsSuccess)
            return AuthResultHelper.HandleAuthFailure(this, result);
        return Ok(result.Value);
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest request, CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _tokenService.LogoutAsync(userId, request.RefreshToken, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, null));
        return NoContent();
    }
}
