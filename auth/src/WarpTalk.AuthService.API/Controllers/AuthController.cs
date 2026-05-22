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
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService)
    {
        _authService = authService;
    }

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
            return AuthResultHelper.HandleAuthFailure(this, result);
        return Ok(result.Value);
    }

    [Authorize]
    [HttpPost("resend-verification")]
    public async Task<IActionResult> ResendVerification(CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await _authService.ResendVerificationAsync(userId, ct);
        if (!result.IsSuccess)
        {
            if (result.ErrorCode == ErrorCodes.RateLimitExceeded || result.ErrorCode == ErrorCodes.CooldownActive)
            {
                return StatusCode(StatusCodes.Status429TooManyRequests, new ApiErrorResponse(result.Error, result.ErrorCode));
            }
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return Ok();
    }

}
