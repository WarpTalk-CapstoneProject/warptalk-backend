using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using WarpTalk.Gateway.Transforms;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// The gateway signs <c>X-Internal-Context</c> with the internal secret, so whatever goes into it
/// arrives downstream as trusted. It used to read <c>workspace_id</c> from the caller's
/// <c>active_workspace_id</c> cookie when the access token had no such claim — and the token never
/// has such a claim, because <c>JwtTokenGenerator</c> does not emit one. So the tenant identity
/// the gateway vouched for was chosen by the browser:
/// <c>document.cookie = 'active_workspace_id=&lt;any guid&gt;'</c>.
/// </summary>
public sealed class InternalContextClaimSourceTests
{
    private const string CallerCookieName = "active_workspace_id";
    private static readonly string AttackerChosenWorkspace = Guid.NewGuid().ToString();
    private static readonly string TokenIssuedWorkspace = Guid.NewGuid().ToString();

    [Fact]
    public void A_workspace_id_cookie_is_never_vouched_for()
    {
        var httpContext = AuthenticatedRequest(
            workspaceClaim: null,
            workspaceCookie: AttackerChosenWorkspace);

        // No claim means nothing to vouch for, so no header is emitted at all.
        Assert.Null(InternalContextTransformProvider.BuildInternalClaims(httpContext));
    }

    [Fact]
    public void A_workspace_id_cookie_cannot_override_the_one_in_the_token()
    {
        var httpContext = AuthenticatedRequest(
            workspaceClaim: TokenIssuedWorkspace,
            workspaceCookie: AttackerChosenWorkspace);

        var claims = InternalContextTransformProvider.BuildInternalClaims(httpContext);

        Assert.NotNull(claims);
        var vouched = Assert.Single(claims!, claim => claim.Type == "workspace_id");
        Assert.Equal(TokenIssuedWorkspace, vouched.Value);
        Assert.NotEqual(AttackerChosenWorkspace, vouched.Value);
    }

    [Fact]
    public void A_token_carrying_a_workspace_id_is_still_vouched_for()
    {
        var httpContext = AuthenticatedRequest(
            workspaceClaim: TokenIssuedWorkspace,
            workspaceCookie: null,
            role: "admin",
            membershipType: "internal");

        var claims = InternalContextTransformProvider.BuildInternalClaims(httpContext);

        Assert.NotNull(claims);
        Assert.Equal(TokenIssuedWorkspace, Value(claims!, "workspace_id"));
        Assert.Equal("admin", Value(claims!, "role"));
        Assert.Equal("internal", Value(claims!, "membership_type"));
    }

    [Fact]
    public void An_unauthenticated_caller_is_never_vouched_for()
    {
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        httpContext.Request.Headers.Cookie = $"{CallerCookieName}={AttackerChosenWorkspace}";

        Assert.Null(InternalContextTransformProvider.BuildInternalClaims(httpContext));
    }

    private static string? Value(IReadOnlyList<Claim> claims, string type) =>
        claims.FirstOrDefault(claim => claim.Type == type)?.Value;

    private static DefaultHttpContext AuthenticatedRequest(
        string? workspaceClaim,
        string? workspaceCookie,
        string? role = null,
        string? membershipType = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())
        };

        if (workspaceClaim is not null) claims.Add(new Claim("workspace_id", workspaceClaim));
        if (role is not null) claims.Add(new Claim("role", role));
        if (membershipType is not null) claims.Add(new Claim("membership_type", membershipType));

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"))
        };

        if (workspaceCookie is not null)
        {
            httpContext.Request.Headers.Cookie = $"{CallerCookieName}={workspaceCookie}";
        }

        return httpContext;
    }
}
