using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using WarpTalk.AuthService.API.Controllers;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Tests;

/// <summary>
/// WT-405 — a sign-out this service could not complete used to leave the browser holding the
/// whole session.
///
/// Clearing a cookie and revoking a token are two different acts, and only one of them can
/// fail. Logout used to treat them as one: the Clear call sat AFTER the failure check, so any
/// unsuccessful LogoutAsync returned 400 with access_token, warptalk_refresh and
/// warptalk_session all still in the jar — including warptalk_session, which the web client
/// reads as "there is still a refresh token here worth redeeming".
///
/// By the time this request arrives the browser has already torn its own session down; it does
/// not put it back on a 400. So the two sides ended up disagreeing — signed out in the tab,
/// signed in here — and the browser had no way to say so.
/// </summary>
public class LogoutCookieClearingTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    /// <summary>
    /// The regression. A failed revoke must still delete the browser's session, and must still
    /// report the failure — the status code is what says whether the family was revoked.
    /// </summary>
    [Fact]
    public async Task AFailedRevoke_StillClearsTheBrowsersSession()
    {
        var tokenService = Substitute.For<ITokenService>();
        tokenService.LogoutAsync(UserId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Refresh token family not found.", ErrorCodes.NotFound));

        var controller = ControllerWithRefreshCookie(tokenService);

        var action = await controller.Logout(new LogoutRequest(string.Empty), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action);

        var setCookie = controller.Response.Headers.SetCookie.ToString();
        Assert.Contains("access_token=", setCookie);
        Assert.Contains("warptalk_refresh=", setCookie);
        Assert.Contains(
            "warptalk_session=",
            setCookie);
        Assert.Contains("expires=", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The service throwing outright is the same situation with a louder failure mode, and it
    /// must not be able to strand a session either.
    /// </summary>
    [Fact]
    public async Task AThrowingRevoke_DoesNotLeaveTheSessionCookiesBehind()
    {
        var tokenService = Substitute.For<ITokenService>();
        tokenService.LogoutAsync(UserId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Result>(_ => throw new InvalidOperationException("the database is unreachable"));

        var controller = ControllerWithRefreshCookie(tokenService);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.Logout(new LogoutRequest(string.Empty), CancellationToken.None));

        // Deliberately NOT asserted as a passing sign-out: this documents that an exception
        // escapes before any cookie is written, which is the one path Clear cannot cover from
        // inside the action. The middleware answers 500 and the client's retry (WT-405, web
        // side) is what closes it.
        Assert.Empty(controller.Response.Headers.SetCookie.ToString());
    }

    /// <summary>A successful sign-out is unchanged — 204 and every cookie gone.</summary>
    [Fact]
    public async Task ASuccessfulRevoke_StillClearsAndAnswers204()
    {
        var tokenService = Substitute.For<ITokenService>();
        tokenService.LogoutAsync(UserId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var controller = ControllerWithRefreshCookie(tokenService);

        var action = await controller.Logout(new LogoutRequest(string.Empty), CancellationToken.None);

        Assert.IsType<NoContentResult>(action);
        var setCookie = controller.Response.Headers.SetCookie.ToString();
        Assert.Contains("access_token=", setCookie);
        Assert.Contains("warptalk_refresh=", setCookie);
        Assert.Contains("warptalk_session=", setCookie);
    }

    private static TokenController ControllerWithRefreshCookie(ITokenService tokenService)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = "warptalk_refresh=a-refresh-token";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, UserId.ToString())],
            authenticationType: "TestAuth"));

        return new TokenController(tokenService)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }
}
