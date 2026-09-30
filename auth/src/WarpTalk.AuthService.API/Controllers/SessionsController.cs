using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.AuthService.API.Common;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.AuthService.API.Controllers;

/// <summary>
/// The caller's own signed-in sessions: list, end one, end all the others.
///
/// A session is a refresh-token family. "Which one is this device" is answered from the
/// warptalk_refresh cookie, whose Path (/api/v1/auth) puts it on every request here — see
/// TokenService.ResolveCurrentFamilyAsync for why that, and not a new access-token claim.
///
/// There is no endpoint here for ending the CURRENT session. That is POST /auth/logout, which
/// clears the cookies in the same response; this controller refuses it with 409.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/auth/sessions")]
public class SessionsController : ControllerBase
{
    private readonly ITokenService _tokenService;

    public SessionsController(ITokenService tokenService)
    {
        _tokenService = tokenService;
    }

    [HttpGet]
    public async Task<IActionResult> GetSessions(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _tokenService.GetSessionsAsync(userId.Value, CurrentRefreshToken(), ct);
        return result.IsSuccess ? Ok(result.Value) : ToError(result);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> RevokeSession(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _tokenService.RevokeSessionAsync(userId.Value, id, CurrentRefreshToken(), ct);
        return result.IsSuccess ? NoContent() : ToError(result);
    }

    [HttpPost("revoke-others")]
    public async Task<IActionResult> RevokeOtherSessions(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _tokenService.RevokeOtherSessionsAsync(userId.Value, CurrentRefreshToken(), ct);
        return result.IsSuccess ? NoContent() : ToError(result);
    }

    private string? CurrentRefreshToken() => Request.Cookies[AuthSessionCookies.RefreshCookieName];

    private IActionResult ToError(Result result)
    {
        var body = new ApiErrorResponse(result.Error, result.ErrorCode);
        return result.ErrorCode switch
        {
            ErrorCodes.NotFound => NotFound(body),
            ErrorCodes.Conflict => Conflict(body),
            ErrorCodes.ValidationError => BadRequest(body),
            _ => StatusCode(500, body),
        };
    }
}
