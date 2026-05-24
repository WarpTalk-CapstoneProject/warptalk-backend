using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.API.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class AuthController : BaseApiController
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService)
    {
        _authService = authService;
    }

    [HttpPost("register")]
    public async Task<Result<AuthResponse>> Register([FromBody] RegisterRequest request, CancellationToken ct)
        => await _authService.RegisterAsync(request, ct);

    [HttpPost("login")]
    public async Task<Result<AuthResponse>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var loginRequest = request with
        {
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            DeviceInfo = Request.Headers.UserAgent.ToString()
        };
        return await _authService.LoginAsync(loginRequest, ct);
    }

    [Authorize]
    [HttpPost("resend-verification")]
    public async Task<Result> ResendVerification(CancellationToken ct)
        => await _authService.ResendVerificationAsync(CurrentUserId, ct);
}

