using System.Security.Claims;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.Gateway.Monitoring;

/// <summary>
/// Admits a WarpTalk system administrator into the embedded Grafana, and nobody else.
///
/// HOW THE EMBED IS AUTHENTICATED
///     Grafana is served at <c>https://{app domain}/grafana/</c>, the same origin as the admin
///     portal, so the System Health page can iframe it. Traefik runs every request for that path
///     through a ForwardAuth middleware pointed at <see cref="Path"/>. This endpoint validates the
///     caller's WarpTalk JWT with exactly the gateway's own validation (signing keys, issuer,
///     audience, lifetime), requires the system-admin policy every <c>/api/v1/admin/*</c> endpoint
///     uses, and answers 200 carrying <see cref="UserHeader"/>. Traefik copies that header onto the
///     request it forwards; Grafana runs <c>auth.proxy</c> and trusts it — only from the pod
///     network, and a NetworkPolicy admits nothing but Traefik and the monitoring namespace to the
///     Grafana pod. Anything else — no token, an expired token, a workspace admin — gets 401/403
///     and never reaches Grafana. There is no anonymous Grafana and no second password.
///
/// WHY THE COOKIE, AND ONLY HERE
///     An iframe cannot set an Authorization header. The web client already keeps the current
///     access token in the host-only <c>access_token</c> cookie (SameSite=Lax, refreshed with the
///     token), and a same-origin iframe sends it. Everywhere else the gateway refuses to read a
///     JWT from a cookie, and that stays true: <see cref="TryReadCookieToken"/> answers only for
///     this one path, and only when no Authorization header was sent.
///
/// WHY IT IS NOT RATE LIMITED
///     Opening a dashboard is a burst of a hundred-odd asset and query requests, each of which
///     passes through here. Counted against the per-user budget that also serves the API, one
///     dashboard would lock its viewer out of the whole product (the presence retry storm did
///     exactly that). The endpoint does nothing but validate a token it was handed.
/// </summary>
public static class GrafanaForwardAuth
{
    public const string Path = "/internal/grafana/auth";
    public const string AccessTokenCookie = "access_token";
    public const string UserHeader = "X-WEBAUTH-USER";

    /// <summary>
    /// The JWT to authenticate a ForwardAuth call with, taken from the access-token cookie.
    /// False for every other path, and whenever an Authorization header is present.
    /// </summary>
    public static bool TryReadCookieToken(HttpRequest request, out string token)
    {
        token = string.Empty;
        if (!request.Path.Equals(Path, StringComparison.OrdinalIgnoreCase)) return false;
        if (request.Headers.ContainsKey("Authorization")) return false;
        if (!request.Cookies.TryGetValue(AccessTokenCookie, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        token = value;
        return true;
    }

    /// <summary>
    /// The Grafana login for this user: their email, or their id when the token carries no email.
    ///
    /// Never a bare word. Grafana's built-in administrator is the login <c>admin</c>; an email
    /// always contains '@' and a user id is a GUID, so no WarpTalk identity can be mapped onto it.
    /// Anything that could break a header (control characters, non-ASCII) is refused outright.
    /// </summary>
    public static string? GrafanaLogin(ClaimsPrincipal user)
    {
        var email = user.GetEmail();
        if (!string.IsNullOrWhiteSpace(email) && email.Contains('@') && IsHeaderSafe(email))
        {
            return email.Trim().ToLowerInvariant();
        }

        return user.GetUserId() is { } id ? id.ToString() : null;
    }

    public static IEndpointRouteBuilder MapGrafanaForwardAuth(this IEndpointRouteBuilder app)
    {
        // Any method: Traefik asks with the method of the request it is authorising, and Grafana's
        // panel queries are POSTs.
        app.Map(Path, (HttpContext context, ClaimsPrincipal user) =>
            {
                var login = GrafanaLogin(user);
                if (login is null)
                {
                    return Results.Forbid();
                }

                context.Response.Headers[UserHeader] = login;
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok();
            })
            .RequireAuthorization(SystemAdminAuthorization.PolicyName)
            .DisableRateLimiting()
            .ExcludeFromDescription()
            .WithName("GrafanaForwardAuth");

        return app;
    }

    private static bool IsHeaderSafe(string value) =>
        value.All(c => c > 0x20 && c < 0x7F);
}
