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
}
