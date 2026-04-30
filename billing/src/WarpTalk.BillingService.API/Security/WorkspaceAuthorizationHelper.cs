using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace WarpTalk.BillingService.API.Security;

public static class WorkspaceAuthorizationHelper
{
    private static readonly HashSet<string> AdminOrServiceRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin",
        "owner",
        "service",
        "system"
    };

    private static readonly string[] WorkspaceClaimTypes =
    [
        "workspace_id",
        "workspaceId",
        "workspace",
        "tenant_id",
        "tenantId"
    ];

    public static IActionResult? ValidateWorkspaceAccess(HttpContext context, Guid workspaceId)
    {
        if (workspaceId == Guid.Empty)
        {
            return new BadRequestObjectResult(new { message = "X-Workspace-Id header is required." });
        }

        var requireAuthentication = bool.TryParse(
            context.RequestServices.GetService<IConfiguration>()?["Security:RequireAuthentication"],
            out var configuredValue) && configuredValue;

        if (!requireAuthentication)
        {
            return null;
        }

        var user = context.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return new UnauthorizedObjectResult(new { message = "Authentication required." });
        }

        if (user.Claims.Any(c => c.Type == ClaimTypes.Role && AdminOrServiceRoles.Contains(c.Value)))
        {
            return null;
        }

        var workspaceClaim = user.Claims.FirstOrDefault(c => WorkspaceClaimTypes.Contains(c.Type));
        if (workspaceClaim == null)
        {
            return null;
        }

        if (!Guid.TryParse(workspaceClaim.Value, out var claimedWorkspaceId) || claimedWorkspaceId != workspaceId)
        {
            return new ForbidResult();
        }

        return null;
    }
}
