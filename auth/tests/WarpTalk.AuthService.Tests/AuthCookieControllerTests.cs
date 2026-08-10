using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using WarpTalk.AuthService.API.Controllers;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Domain.Enums;
using WarpTalk.Shared;
using System.Text.Json;
using System.Security.Claims;
using System.Reflection;
using WarpTalk.AuthService.API.Validators;

namespace WarpTalk.AuthService.Tests;

public class AuthCookieControllerTests
{
    [Fact]
    public async Task RefreshBodyValidator_AllowsMissingTokenForHttpOnlyCookieFallback()
    {
        var result = await new RefreshTokenRequestValidator()
            .ValidateAsync(new RefreshTokenRequest(null, null, null));

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task LogoutBodyValidator_AllowsMissingTokenForHttpOnlyCookieFallback()
    {
        var result = await new LogoutRequestValidator()
            .ValidateAsync(new LogoutRequest(string.Empty));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void LogoutBody_AllowsModelBindingWithoutLegacyTokenField()
    {
        var property = typeof(LogoutRequest).GetProperty(nameof(LogoutRequest.RefreshToken));
        Assert.NotNull(property);

        var nullability = new NullabilityInfoContext().Create(property);
        Assert.Equal(NullabilityState.Nullable, nullability.ReadState);
    }

    [Fact]
    public async Task Login_Success_WritesHttpOnlySecureSessionCookies()
    {
        var authService = Substitute.For<IAuthService>();
        var authResponse = CreateAuthResponse();
        authService.LoginAsync(Arg.Any<LoginRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(authResponse));

        var controller = new AuthController(authService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.Login(
            new LoginRequest("user@example.com", "Password123!", null, null),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var setCookie = controller.Response.Headers.SetCookie.ToString();
        Assert.Contains("warptalk_refresh=refresh-token", setCookie);
        Assert.Contains("warptalk_session=active", setCookie);
        Assert.Contains("access_token=access-token", setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_Success_DoesNotExposeRefreshTokenInJsonBody()
    {
        var authService = Substitute.For<IAuthService>();
        authService.LoginAsync(Arg.Any<LoginRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(CreateAuthResponse()));
        var controller = new AuthController(authService)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var action = await controller.Login(
            new LoginRequest("user@example.com", "Password123!", null, null),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.DoesNotContain("refreshToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("accessToken", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_BehindGateway_SharesCookiesAcrossWarpTalkSubdomains()
    {
        var authService = Substitute.For<IAuthService>();
        authService.LoginAsync(Arg.Any<LoginRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(CreateAuthResponse()));
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("auth-service", 5101);
        context.Request.Headers["X-Forwarded-Host"] = "api.warptalk.io.vn";
        var controller = new AuthController(authService)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        await controller.Login(
            new LoginRequest("user@example.com", "Password123!", null, null),
            CancellationToken.None);

        Assert.Contains(
            "domain=.warptalk.io.vn",
            controller.Response.Headers.SetCookie.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_UsesGatewayAppendedForwardedHostInsteadOfClientPrefix()
    {
        var authService = Substitute.For<IAuthService>();
        authService.LoginAsync(Arg.Any<LoginRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(CreateAuthResponse()));
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("auth-service", 5101);
        context.Request.Headers["X-Forwarded-Host"] = "attacker.example, api.warptalk.io.vn";
        var controller = new AuthController(authService)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        await controller.Login(
            new LoginRequest("user@example.com", "Password123!", null, null),
            CancellationToken.None);

        Assert.Contains(
            "domain=.warptalk.io.vn",
            controller.Response.Headers.SetCookie.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refresh_Success_RotatesHttpOnlySecureSessionCookies()
    {
        var tokenService = Substitute.For<ITokenService>();
        var authResponse = CreateAuthResponse();
        tokenService.RefreshTokenAsync(Arg.Any<RefreshTokenRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(authResponse));

        var controller = new TokenController(tokenService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.Refresh(
            new RefreshTokenRequest("old-refresh-token", null, null),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var setCookie = controller.Response.Headers.SetCookie.ToString();
        Assert.Contains("warptalk_refresh=refresh-token", setCookie);
        Assert.Contains("access_token=access-token", setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refresh_UsesHttpOnlyCookieWhenBodyDoesNotContainToken()
    {
        var tokenService = Substitute.For<ITokenService>();
        tokenService.RefreshTokenAsync(
                Arg.Is<RefreshTokenRequest>(request => request.RefreshToken == "cookie-refresh-token"),
                Arg.Any<CancellationToken>())
            .Returns(Result.Success(CreateAuthResponse()));
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = "warptalk_refresh=cookie-refresh-token";
        var controller = new TokenController(tokenService)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var action = await controller.Refresh(
            new RefreshTokenRequest(string.Empty, null, null),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(action);
    }

    [Fact]
    public async Task Refresh_RejectedCookie_ClearsDeadBrowserSession()
    {
        var tokenService = Substitute.For<ITokenService>();
        tokenService.RefreshTokenAsync(Arg.Any<RefreshTokenRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AuthResponse>("Invalid refresh token.", "INVALID_REFRESH_TOKEN"));
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = "warptalk_refresh=rejected-refresh-token";
        var controller = new TokenController(tokenService)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var action = await controller.Refresh(
            new RefreshTokenRequest(string.Empty, null, null),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action);
        var setCookie = controller.Response.Headers.SetCookie.ToString();
        Assert.Contains("warptalk_refresh=", setCookie);
        Assert.Contains("warptalk_session=", setCookie);
        Assert.Contains("access_token=", setCookie);
        Assert.Contains("expires=", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// WT-344 — the bug that logged real users out of production during our own deploys.
    ///
    /// RefreshTokenAsync's catch-all returns InternalServerError when the database is
    /// unreachable, and this endpoint used to answer EVERY failure with 400 plus a cookie
    /// wipe. So a few seconds of DB unavailability told every open browser "your refresh
    /// token is invalid" — and the web client, correctly, reads a 4xx from this endpoint as a
    /// dead session and signs the user out. A rolling deploy did this about sixty seconds in.
    ///
    /// Both halves are asserted because either one alone still loses the session: 503 keeps
    /// the client from concluding anything (it already treats 5xx as transient), and NOT
    /// clearing the cookies keeps the refresh token that is, as far as anyone knows, still
    /// perfectly good.
    /// </summary>
    [Fact]
    public async Task Refresh_ServiceFault_Returns503AndKeepsTheSessionCookies()
    {
        var tokenService = Substitute.For<ITokenService>();
        tokenService.RefreshTokenAsync(Arg.Any<RefreshTokenRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AuthResponse>(
                "An unexpected error occurred while refreshing the token.",
                ErrorCodes.InternalServerError));
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = "warptalk_refresh=perfectly-good-refresh-token";
        var controller = new TokenController(tokenService)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var action = await controller.Refresh(
            new RefreshTokenRequest(string.Empty, null, null),
            CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(action);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status.StatusCode);
        // Nothing may be deleted here. An empty Set-Cookie is the assertion: the browser keeps
        // what it had.
        Assert.Empty(controller.Response.Headers.SetCookie.ToString());
    }

    /// <summary>
    /// The other side of the same rule: a token this service actually looked at and refused
    /// still ends the session, cookies and all. Losing this would leave a genuinely dead
    /// session retrying forever instead of landing on the login screen.
    /// </summary>
    [Fact]
    public async Task Refresh_ServiceUnavailable_IsAlsoTreatedAsTransient()
    {
        var tokenService = Substitute.For<ITokenService>();
        tokenService.RefreshTokenAsync(Arg.Any<RefreshTokenRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AuthResponse>(
                "Could not reach the account directory.",
                ErrorCodes.ServiceUnavailable));
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = "warptalk_refresh=perfectly-good-refresh-token";
        var controller = new TokenController(tokenService)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var action = await controller.Refresh(
            new RefreshTokenRequest(string.Empty, null, null),
            CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(action);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status.StatusCode);
        Assert.Empty(controller.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public async Task Logout_UsesRefreshCookieAndClearsSessionCookies()
    {
        var userId = Guid.NewGuid();
        var tokenService = Substitute.For<ITokenService>();
        tokenService.LogoutAsync(userId, "cookie-refresh-token", Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = "warptalk_refresh=cookie-refresh-token";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            "test"));
        var controller = new TokenController(tokenService)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var action = await controller.Logout(
            new LogoutRequest(string.Empty),
            CancellationToken.None);

        Assert.IsType<NoContentResult>(action);
        await tokenService.Received(1).LogoutAsync(
            userId,
            "cookie-refresh-token",
            Arg.Any<CancellationToken>());
        var setCookie = controller.Response.Headers.SetCookie.ToString();
        Assert.Contains("warptalk_refresh=", setCookie);
        Assert.Contains("access_token=", setCookie);
        Assert.Contains("expires=", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GoogleLogin_Success_WritesCookiesWithoutExposingRefreshToken()
    {
        var googleAuthService = Substitute.For<IGoogleAuthService>();
        googleAuthService.GoogleLoginAsync(Arg.Any<GoogleLoginRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(CreateAuthResponse()));
        var controller = new GoogleAuthController(googleAuthService)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var action = await controller.GoogleLogin(
            new GoogleLoginRequest("google-id-token", null, null),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        Assert.DoesNotContain(
            "refreshToken",
            JsonSerializer.Serialize(ok.Value),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("warptalk_refresh=refresh-token", controller.Response.Headers.SetCookie.ToString());
    }

    private static AuthResponse CreateAuthResponse() => new(
        "access-token",
        "refresh-token",
        DateTime.UtcNow.AddMinutes(15),
        new UserDto(
            Guid.NewGuid(),
            "user@example.com",
            "User",
            null,
            null,
            "vi-VN",
            "Asia/Ho_Chi_Minh",
            true,
            AccountStatus.ACTIVE,
            ["user"]));
}
