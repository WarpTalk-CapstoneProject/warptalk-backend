using System.Security.Claims;

namespace WarpTalk.Gateway.Services;

public static class RequestRateLimitPartitionKeys
{
    public static string Ip(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString()
        ?? context.Request.Headers.Host.ToString()
        ?? "unknown";

    public static string User(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? context.User.FindFirstValue("sub")
        ?? $"anonymous:{Ip(context)}";

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
