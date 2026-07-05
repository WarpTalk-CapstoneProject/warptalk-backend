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
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [HttpPost("register-invited")]
    public async Task<IActionResult> RegisterInvited([FromBody] RegisterInvitedRequest request, CancellationToken ct)
    {
        var result = await _authService.RegisterInvitedAsync(request, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
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
        {
            if (result.ErrorCode == ErrorCodes.InvalidCredentials)
            {
                return Unauthorized(new ApiErrorResponse(result.Error, result.ErrorCode));
            }
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return Ok(result.Value);
    }

    [Authorize]
    [HttpPost("resend-verification")]
    public async Task<IActionResult> ResendVerification(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _authService.ResendVerificationAsync(userId.Value, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        return NoContent();
    }
}
