using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.API.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService) => _authService = authService;

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken ct)
    {
        var result = await _authService.RegisterAsync(request, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        return Ok(result.Value);
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var loginRequest = request with
        {
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            DeviceInfo = Request.Headers.UserAgent.ToString()
        };

        var result = await _authService.LoginAsync(loginRequest, ct);
        if (!result.IsSuccess)
            return Unauthorized(new ApiErrorResponse(result.Error, result.ErrorCode));
        return Ok(result.Value);
    }

    [HttpPost("google-login")]
    public async Task<IActionResult> GoogleLogin([FromBody] GoogleLoginRequest request, CancellationToken ct)
    {
        var loginRequest = request with
        {
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            DeviceInfo = Request.Headers.UserAgent.ToString()
        };

        var result = await _authService.GoogleLoginAsync(loginRequest, ct);
        if (!result.IsSuccess)
            return Unauthorized(new ApiErrorResponse(result.Error, result.ErrorCode));
        return Ok(result.Value);
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequest request, CancellationToken ct)
    {
        var refreshRequest = request with
        {
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            DeviceInfo = Request.Headers.UserAgent.ToString()
        };

        var result = await _authService.RefreshTokenAsync(refreshRequest, ct);
        if (!result.IsSuccess)
            return Unauthorized(new ApiErrorResponse(result.Error, result.ErrorCode));
        return Ok(result.Value);
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest request, CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _authService.LogoutAsync(userId, request.RefreshToken, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, null));
        return NoContent();
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> GetProfile(CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _authService.GetProfileAsync(userId, ct);
        if (!result.IsSuccess)
            return NotFound(new ApiErrorResponse(result.Error, result.ErrorCode));
        return Ok(result.Value);
    }

    [Authorize]
    [HttpPut("me")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request, CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _authService.UpdateProfileAsync(userId, request, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        return Ok(result.Value);
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _authService.ChangePasswordAsync(userId, request, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        return NoContent();
    }
}

public record LogoutRequest(string RefreshToken);
