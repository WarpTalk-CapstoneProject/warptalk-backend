using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Security.Claims;
using System.Threading.Tasks;
using WarpTalk.Shared.Extensions;
using WarpTalk.Shared.Protos;

namespace WarpTalk.BillingService.API.Filters;

public class RequireWorkspaceRoleAttribute : TypeFilterAttribute
{
    public RequireWorkspaceRoleAttribute(params string[] allowedRoles) : base(typeof(RequireWorkspaceRoleFilter))
    {
        Arguments = new object[] { allowedRoles };
    }
}

public class RequireWorkspaceRoleFilter : IAsyncActionFilter
{
    private readonly WorkspaceService.WorkspaceServiceClient _workspaceClient;
    private readonly IMemoryCache _cache;
    private readonly string[] _allowedRoles;

    private record CachedMemberDetails(bool IsMember, string RoleName, bool IsActive);

    public RequireWorkspaceRoleFilter(
        WorkspaceService.WorkspaceServiceClient workspaceClient,
        IMemoryCache cache,
        string[] allowedRoles)
    {
        _workspaceClient = workspaceClient;
        _cache = cache;
        _allowedRoles = allowedRoles;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // System Admin bypasses all workspace-specific role checks
        var email = context.HttpContext.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value 
                    ?? context.HttpContext.User.FindFirst("email")?.Value;
        var isSystemAdmin = email == "admin@warptalk.com" || 
                            context.HttpContext.User.IsInRole("Admin") || 
                            context.HttpContext.User.FindFirst("role")?.Value == "Admin";

        if (isSystemAdmin)
        {
            await next();
            return;
        }

        var userId = context.HttpContext.User.GetUserId();
        if (userId == null)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        Guid? workspaceId = null;
        if (context.RouteData.Values.TryGetValue("workspaceId", out var wsVal) &&
            Guid.TryParse(wsVal?.ToString(), out var parsedWsId))
        {
            workspaceId = parsedWsId;
        }
        else if (context.ActionArguments.TryGetValue("request", out var reqObj))
        {
            var wsProp = reqObj?.GetType().GetProperty("WorkspaceId");
            if (wsProp != null)
            {
                var val = wsProp.GetValue(reqObj);
                if (val is Guid g) workspaceId = g;
            }
        }

        if (workspaceId == null)
        {
            await next();
            return;
        }

        // Allow users to access their own personal credits/wallet sandbox (workspaceId == userId)
        if (workspaceId == userId)
        {
            await next();
            return;
        }

        CachedMemberDetails? memberDetails = null;
        var cacheKey = $"role-auth:{userId}:{workspaceId}";

        if (!_cache.TryGetValue(cacheKey, out memberDetails))
        {
            try
            {
                var response = await _workspaceClient.GetWorkspaceMemberDetailsAsync(new GetWorkspaceMemberRequest
                {
                    WorkspaceId = workspaceId.Value.ToString(),
                    UserId = userId.Value.ToString()
                });

                memberDetails = new CachedMemberDetails(
                    response.IsMember,
                    response.RoleName ?? "Member",
                    string.Equals(response.MembershipType ?? "internal", "internal", StringComparison.OrdinalIgnoreCase)
                        ? response.IsActive
                        : response.IsActive // standard behavior
                );

                // Cache authorization details for 60 seconds
                _cache.Set(cacheKey, memberDetails, TimeSpan.FromSeconds(60));
            }
            catch
            {
                context.Result = new StatusCodeResult(500);
                return;
            }
        }

        if (memberDetails == null || !memberDetails.IsMember || !memberDetails.IsActive)
        {
            context.Result = new ContentResult { StatusCode = 403, Content = "Access denied. You are not an active member of this workspace." };
            return;
        }

        var isAllowed = false;
        foreach (var role in _allowedRoles)
        {
            if (string.Equals(memberDetails.RoleName, role, StringComparison.OrdinalIgnoreCase))
            {
                isAllowed = true;
                break;
            }
        }

        if (!isAllowed)
        {
            context.Result = new ContentResult { StatusCode = 403, Content = "Access denied. Insufficient permissions to view or manage billing." };
            return;
        }

        await next();
    }
}
