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
        var registration = result.Value!;

        // BR-02 — cookies only when there is a real session to write. An unverified account gets
        // told to check its email, and nothing that could be mistaken for a signed-in state.
        if (registration.Auth is null)
        {
            return Ok(new { emailVerificationRequired = true });
        }

        AuthSessionCookies.Write(Request, Response, registration.Auth);
        return Ok(AuthSessionCookies.ToResponse(registration.Auth));
    }

    [HttpPost("register-invited")]
    public async Task<IActionResult> RegisterInvited([FromBody] RegisterInvitedRequest request, CancellationToken ct)
    {
        var result = await _authService.RegisterInvitedAsync(request, ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        }
        var auth = result.Value!;
        AuthSessionCookies.Write(Request, Response, auth);
        return Ok(AuthSessionCookies.ToResponse(auth));
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
        var auth = result.Value!;
        AuthSessionCookies.Write(Request, Response, auth);
        return Ok(AuthSessionCookies.ToResponse(auth));
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

    [AllowAnonymous]
    [HttpPost("verify-email")]
    public async Task<IActionResult> VerifyEmail(
        [FromBody] VerifyEmailRequest request,
        CancellationToken ct)
    {
        var result = await _authService.VerifyEmailAsync(request, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        return NoContent();
    }

    [AllowAnonymous]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(
        [FromBody] ForgotPasswordRequest request,
        CancellationToken ct)
    {
        await _authService.ForgotPasswordAsync(request, ct);
        return NoContent();
    }

    [AllowAnonymous]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(
        [FromBody] ResetPasswordRequest request,
        CancellationToken ct)
    {
        var result = await _authService.ResetPasswordAsync(request, ct);
        if (!result.IsSuccess)
            return BadRequest(new ApiErrorResponse(result.Error, result.ErrorCode));
        return NoContent();
    }
}
