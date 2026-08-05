using Microsoft.AspNetCore.Http;
using WarpTalk.AuthService.Application.DTOs;

namespace WarpTalk.AuthService.API.Common;

internal static class AuthSessionCookies
{
    internal const string AccessCookieName = "access_token";
    internal const string RefreshCookieName = "warptalk_refresh";
    internal const string SessionCookieName = "warptalk_session";

    internal static void Write(HttpRequest request, HttpResponse response, AuthResponse auth)
    {
        var (domain, secure) = ResolveCookieScope(request);

        response.Cookies.Append(AccessCookieName, auth.AccessToken, new CookieOptions
        {
            Domain = domain,
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = auth.ExpiresAt
        });
        response.Cookies.Append(RefreshCookieName, auth.RefreshToken, new CookieOptions
        {
            Domain = domain,
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Lax,
            Path = "/api/v1/auth",
            Expires = DateTimeOffset.UtcNow.AddDays(7)
        });
        response.Cookies.Append(SessionCookieName, "active", new CookieOptions
        {
            Domain = domain,
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.AddDays(7)
        });
    }

    internal static void Clear(HttpRequest request, HttpResponse response)
    {
        var (domain, secure) = ResolveCookieScope(request);
        response.Cookies.Delete(AccessCookieName, new CookieOptions
        {
            Domain = domain,
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Lax,
            Path = "/"
        });
        response.Cookies.Delete(RefreshCookieName, new CookieOptions
        {
            Domain = domain,
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Lax,
            Path = "/api/v1/auth"
        });
        response.Cookies.Delete(SessionCookieName, new CookieOptions
        {
            Domain = domain,
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Lax,
            Path = "/"
        });
    }

    internal static AuthSessionResponse ToResponse(AuthResponse auth) => new(
        auth.AccessToken,
        auth.ExpiresAt,
        auth.User);

    private static (string? Domain, bool Secure) ResolveCookieScope(HttpRequest request)
    {
        var forwardedHost = request.Headers["X-Forwarded-Host"]
            .ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        var host = string.IsNullOrWhiteSpace(forwardedHost)
            ? request.Host.Host
            : new HostString(forwardedHost).Host;
        var secure = !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            && host != "127.0.0.1"
            && host != "::1";
        var domain = host.EndsWith(".warptalk.io.vn", StringComparison.OrdinalIgnoreCase)
            ? ".warptalk.io.vn"
            : null;
        return (domain, secure);
    }
}

internal sealed record AuthSessionResponse(
    string AccessToken,
    DateTime ExpiresAt,
    UserDto User);
