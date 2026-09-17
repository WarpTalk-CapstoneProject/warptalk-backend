using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.BillingService.API.Authorization;

/// <summary>
/// Lets any active INTERNAL member of the workspace through, whatever their workspace role.
/// EXTERNAL members are guests from another organisation and are refused. Platform system
/// admins pass, as they do on <see cref="RequireWorkspaceRoleAttribute"/> when it allows
/// SystemAdmin. WT-700.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireInternalWorkspaceMemberAttribute : TypeFilterAttribute
{
    public RequireInternalWorkspaceMemberAttribute()
        : base(typeof(RequireInternalWorkspaceMemberFilter))
    {
    }
}

internal sealed class RequireInternalWorkspaceMemberFilter : IAsyncActionFilter
{
    // The proto documents "INTERNAL"/"EXTERNAL", but workspace-service also produces the enum
    // name ("Internal") in places, so the comparison is case-insensitive.
    internal const string InternalMembershipType = "INTERNAL";

    private readonly IWorkspaceClient _workspaceClient;

    public RequireInternalWorkspaceMemberFilter(IWorkspaceClient workspaceClient)
    {
        _workspaceClient = workspaceClient;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.HttpContext.User.IsInRole(WorkspaceRoleConstants.SystemAdmin) ||
            context.HttpContext.User.IsInRole(WorkspaceRoleConstants.Admin))
        {
            await next();
            return;
        }

        var userId = context.HttpContext.User.GetUserId();
        if (userId == null)
        {
            context.Result = new UnauthorizedObjectResult(new ApiErrorResponse(
                ApiMessageConstants.ErrorMessages.UnauthorizedTokenDetail,
                ErrorCodes.Unauthorized));
            return;
        }

        if (!WorkspaceIdResolver.TryGetWorkspaceId(context, out var workspaceId))
        {
            context.Result = new BadRequestObjectResult(new ApiErrorResponse(
                ApiMessageConstants.ValidationMessages.WorkspaceIdRequired,
                ErrorCodes.ValidationError));
            return;
        }

        var memberResult = await _workspaceClient.GetWorkspaceMemberDetailsAsync(
            workspaceId, userId.Value, context.HttpContext.RequestAborted);
        if (!memberResult.IsSuccess)
        {
            context.Result = new ObjectResult(new ApiErrorResponse(
                memberResult.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError,
                memberResult.ErrorCode ?? ErrorCodes.InternalServerError))
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
            return;
        }

        if (!IsActiveInternalMember(memberResult.Value))
        {
            context.Result = new ObjectResult(new ApiErrorResponse(
                ApiMessageConstants.ErrorMessages.BillingAccessDenied,
                ErrorCodes.Forbidden))
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        await next();
    }

    // An empty or unrecognised membership type is refused, not treated as internal: the gate
    // exists to keep external guests out, and a response that cannot say which kind of member
    // this is (older workspace-service, a new type added later) must not open it for them.
    private static bool IsActiveInternalMember(
        (bool IsMember, string RoleName, bool IsActive, string MembershipType) member) =>
        member.IsMember &&
        member.IsActive &&
        string.Equals(member.MembershipType, InternalMembershipType, StringComparison.OrdinalIgnoreCase);
}
