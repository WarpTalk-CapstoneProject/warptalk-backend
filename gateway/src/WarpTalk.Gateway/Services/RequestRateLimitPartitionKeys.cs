using System.Security.Claims;

namespace WarpTalk.Gateway.Services;

public static class RequestRateLimitPartitionKeys
{
    public static string Ip(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString()
        ?? context.Request.Headers.Host.ToString()
        ?? "unknown";

    /// <summary>
    /// Prefix of the value <see cref="User"/> returns when there is no user to key on. Named rather
    /// than spelled out at each call site: a caller that compares against the wrong literal
    /// silently treats every anonymous request as a distinct identity.
    /// </summary>
    public const string AnonymousPrefix = "anonymous:";

    public static string User(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? context.User.FindFirstValue("sub")
        ?? $"{AnonymousPrefix}{Ip(context)}";

    public static string? Workspace(HttpContext context)
    {
        var candidate = context.User.FindFirstValue("workspace_id");
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = context.Request.RouteValues["workspaceId"]?.ToString();
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = context.Request.Headers["X-Workspace-Id"].FirstOrDefault();
        }

        return Guid.TryParse(candidate, out var workspaceId)
            ? workspaceId.ToString()
            : null;
    }
}
