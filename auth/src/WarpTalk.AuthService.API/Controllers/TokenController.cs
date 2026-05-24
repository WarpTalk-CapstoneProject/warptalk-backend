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
public class TokenController : BaseApiController
{
    private readonly ITokenService _tokenService;

    public TokenController(ITokenService tokenService)
    {
        _tokenService = tokenService;
    }

    [HttpPost("refresh")]
    public async Task<Result<AuthResponse>> Refresh([FromBody] RefreshTokenRequest request, CancellationToken ct)
    {
        var refreshRequest = request with
        {
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            DeviceInfo = Request.Headers.UserAgent.ToString()
        };
        return await _tokenService.RefreshTokenAsync(refreshRequest, ct);
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<Result> Logout([FromBody] LogoutRequest request, CancellationToken ct)
        => await _tokenService.LogoutAsync(CurrentUserId, request.RefreshToken, ct);
}

