using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WarpTalk.BillingService.API.Security;

public static class WorkspaceAuthorizationHelper
{
    private const string WorkspaceClaimType = "workspace_id";

    private static readonly HashSet<string> PrivilegedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin",
        "owner",
        "service",
        "system"
    };

    public static IActionResult? ValidateWorkspaceAccess(
        HttpContext context,
        Guid workspaceId)
    {
        if (workspaceId == Guid.Empty)
        {
            return new BadRequestObjectResult(new
            {
                message = "WorkspaceId is required."
            });
        }

        var config = context.RequestServices.GetService<IConfiguration>();
        var logger = context.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger("WorkspaceAuth");

        var requireAuth = config?.GetValue<bool>("Security:RequireAuthentication") ?? false;

        if (!requireAuth)
            return null;

        var user = context.User;

        if (user?.Identity?.IsAuthenticated != true)
        {
            return new UnauthorizedObjectResult(new
            {
                message = "Authentication required."
            });
        }

        // =======================================================
        // 1. PRIVILEGED ROLES
        // =======================================================
        var roles = user.FindAll(ClaimTypes.Role).Select(x => x.Value);

        if (roles.Any(r => PrivilegedRoles.Contains(r)))
        {
            return null;
        }

        // =======================================================
        // 2. WORKSPACE CHECK
        // =======================================================
        var workspaceClaim = user.FindFirst(WorkspaceClaimType);

        if (workspaceClaim == null)
        {
            logger?.LogWarning("Missing workspace claim for user {User}", user.Identity?.Name);
            return new ForbidResult();
        }

        if (!Guid.TryParse(workspaceClaim.Value, out var claimedWorkspaceId))
        {
            logger?.LogWarning("Invalid workspace claim format: {Value}", workspaceClaim.Value);
            return new ForbidResult();
        }

        if (claimedWorkspaceId != workspaceId)
        {
            logger?.LogWarning("Workspace mismatch. Claim={Claimed}, Request={Requested}",
                claimedWorkspaceId, workspaceId);

            return new ForbidResult();
        }

        return null;
    }
}