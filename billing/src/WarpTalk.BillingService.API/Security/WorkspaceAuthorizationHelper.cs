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

    private static readonly HashSet<string> WorkspaceAdminRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin",
        "owner",
        "workspace_admin",
        "workspace-admin",
        "system"
    };

    private static readonly HashSet<string> ServiceRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin",
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

    private static readonly string[] RoleClaimTypes =
    [
        ClaimTypes.Role,
        "role",
        "roles"
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

        if (user.Claims.Any(c => RoleClaimTypes.Contains(c.Type) && AdminOrServiceRoles.Contains(c.Value)))
        {
            return null;
        }

        var workspaceClaim = user.Claims.FirstOrDefault(c => WorkspaceClaimTypes.Contains(c.Type));
        if (workspaceClaim == null)
        {
            return new ForbidResult();
        }

        if (!Guid.TryParse(workspaceClaim.Value, out var claimedWorkspaceId) || claimedWorkspaceId != workspaceId)
        {
            return new ForbidResult();
        }

        return null;
    }

    public static IActionResult? ValidateWorkspaceAdminAccess(HttpContext context, Guid workspaceId)
    {
        var accessError = ValidateWorkspaceAccess(context, workspaceId);
        if (accessError != null)
        {
            return accessError;
        }

        if (!IsAuthenticationRequired(context))
        {
            return null;
        }

        return HasAnyRole(context.User, WorkspaceAdminRoles)
            ? null
            : new ForbidResult();
    }

    public static IActionResult? ValidateServiceAccess(HttpContext context, Guid workspaceId)
    {
        var accessError = ValidateWorkspaceAccess(context, workspaceId);
        if (accessError != null)
        {
            return accessError;
        }

        if (!IsAuthenticationRequired(context))
        {
            return null;
        }

        return HasAnyRole(context.User, ServiceRoles)
            ? null
            : new ForbidResult();
    }

    private static bool IsAuthenticationRequired(HttpContext context)
    {
        return bool.TryParse(
            context.RequestServices.GetService<IConfiguration>()?["Security:RequireAuthentication"],
            out var configuredValue) && configuredValue;
    }

    private static bool HasAnyRole(ClaimsPrincipal user, HashSet<string> allowedRoles)
    {
        return user.Claims.Any(c => RoleClaimTypes.Contains(c.Type) && allowedRoles.Contains(c.Value));
    }
}
