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
public class GoogleAuthController : BaseApiController
{
    private readonly IGoogleAuthService _googleAuthService;

    public GoogleAuthController(IGoogleAuthService googleAuthService)
    {
        _googleAuthService = googleAuthService;
    }

    [HttpPost("google-login")]
    public async Task<Result<AuthResponse>> GoogleLogin([FromBody] GoogleLoginRequest request, CancellationToken ct)
    {
        var loginRequest = request with
        {
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            DeviceInfo = Request.Headers.UserAgent.ToString()
        };
        return await _googleAuthService.GoogleLoginAsync(loginRequest, ct);
    }

    [Authorize]
    [HttpPost("google/link")]
    public async Task<Result> LinkGoogle([FromBody] LinkGoogleRequest request, CancellationToken ct)
        => await _googleAuthService.LinkGoogleAsync(CurrentUserId, request, ct);

    [Authorize]
    [HttpPost("google/unlink")]
    public async Task<Result> UnlinkGoogle(CancellationToken ct)
        => await _googleAuthService.UnlinkGoogleAsync(CurrentUserId, ct);
}

