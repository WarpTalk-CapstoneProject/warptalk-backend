using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WarpTalk.TranslationRoomService.Tests.Integration;

public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string UserIdHeader = "X-Test-UserId";
    public const string WorkspaceIdHeader = "X-Test-WorkspaceId";

    /// <summary>
    /// Optional. Room-read admits a caller who holds a standing email invitation but never joined
    /// (see RoomReadAccess), and that caller is reachable only through an email claim — so a test
    /// that wants to prove what such a caller can and cannot see needs a way to present one.
    /// Omitting the header behaves exactly as before: no email claim at all.
    /// </summary>
    public const string EmailHeader = "X-Test-Email";

    /// <summary>
    /// Optional, comma-separated platform roles (e.g. "admin"), for the system-admin endpoints.
    /// Omitting it behaves exactly as before: no role claims.
    /// </summary>
    public const string RolesHeader = "X-Test-Roles";

    public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new List<Claim>();

        if (Context.Request.Headers.TryGetValue(UserIdHeader, out var userId))
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId[0]!));
        }
        else
        {
            // Default user if no header provided
            claims.Add(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()));
        }

        if (Context.Request.Headers.TryGetValue(EmailHeader, out var email))
        {
            claims.Add(new Claim(ClaimTypes.Email, email[0]!));
        }

        if (Context.Request.Headers.TryGetValue(RolesHeader, out var roles))
        {
            foreach (var role in roles[0]!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }
        }

        claims.Add(new Claim("email_verified", "true"));

        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, "Test");

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
