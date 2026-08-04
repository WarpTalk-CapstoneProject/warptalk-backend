using System.Security.Claims;

namespace WarpTalk.Shared.Extensions;

public static class ClaimsPrincipalExtensions
{
    public static Guid? GetUserId(this ClaimsPrincipal principal)
    {
        var userIdStr = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? principal.FindFirst("sub")?.Value;
        if (Guid.TryParse(userIdStr, out var userId))
        {
            return userId;
        }
        return null;
    }
    //OAuth2
    public static bool IsEmailVerified(this ClaimsPrincipal principal)
    {
        var claim = principal.FindFirst("email_verified")?.Value;
        return bool.TryParse(claim, out var verified) && verified;
    }

    public static string? GetEmail(this ClaimsPrincipal principal)
    {
        return principal.FindFirst(ClaimTypes.Email)?.Value ?? principal.FindFirst("email")?.Value;
    }
}
