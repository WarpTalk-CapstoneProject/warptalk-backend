using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using WarpTalk.Gateway.Monitoring;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// The embedded Grafana's ForwardAuth. The cookie exception is the dangerous part: everywhere
/// else the gateway refuses a JWT from a cookie, so these pin that the exception is exactly one
/// path wide and yields to a real Authorization header.
/// </summary>
public sealed class GrafanaForwardAuthTests
{
    private static HttpRequest Request(string path, string? cookie = null, string? authorization = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (cookie is not null) context.Request.Headers.Cookie = $"access_token={cookie}";
        if (authorization is not null) context.Request.Headers.Authorization = authorization;
        return context.Request;
    }

    [Fact]
    public void TheCookieIsReadOnTheForwardAuthPath()
    {
        Assert.True(GrafanaForwardAuth.TryReadCookieToken(Request(GrafanaForwardAuth.Path, "jwt"), out var token));
        Assert.Equal("jwt", token);
    }

    [Theory]
    [InlineData("/api/v1/admin/platform-health")]
    [InlineData("/internal/grafana/auth/extra")]
    [InlineData("/grafana")]
    [InlineData("/")]
    public void TheCookieIsNeverReadAnywhereElse(string path)
    {
        Assert.False(GrafanaForwardAuth.TryReadCookieToken(Request(path, "jwt"), out _));
    }

    [Fact]
    public void AnAuthorizationHeaderWinsOverTheCookie()
    {
        Assert.False(GrafanaForwardAuth.TryReadCookieToken(
            Request(GrafanaForwardAuth.Path, "cookie-jwt", "Bearer header-jwt"), out _));
    }

    [Fact]
    public void NoCookieMeansNoToken()
    {
        Assert.False(GrafanaForwardAuth.TryReadCookieToken(Request(GrafanaForwardAuth.Path), out _));
    }

    [Fact]
    public void TheGrafanaLoginIsTheLowercasedEmail()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("email", "Ops.Admin@WarpTalk.io.vn"), new Claim("sub", Guid.NewGuid().ToString())],
            "Bearer"));

        Assert.Equal("ops.admin@warptalk.io.vn", GrafanaForwardAuth.GrafanaLogin(user));
    }

    [Fact]
    public void WithoutAUsableEmailTheLoginIsTheUserId_NeverABareWordLikeAdmin()
    {
        var id = Guid.NewGuid();
        foreach (var email in new[] { "admin", "evil\r\nX-Other: 1@x", "ngô@warptalk.vn" })
        {
            var user = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("email", email), new Claim(ClaimTypes.NameIdentifier, id.ToString())],
                "Bearer"));

            Assert.Equal(id.ToString(), GrafanaForwardAuth.GrafanaLogin(user));
        }
    }
}
