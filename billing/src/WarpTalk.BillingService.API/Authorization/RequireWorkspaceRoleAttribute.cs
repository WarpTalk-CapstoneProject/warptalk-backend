using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.BillingService.API.Authorization;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireWorkspaceRoleAttribute : TypeFilterAttribute
{
    public RequireWorkspaceRoleAttribute(params string[] allowedRoles)
        : base(typeof(RequireWorkspaceRoleFilter))
    {
        Arguments = [allowedRoles];
    }
}

internal sealed class RequireWorkspaceRoleFilter : IAsyncActionFilter
{
    private readonly IWorkspaceClient _workspaceClient;
    private readonly IStaffAccessResolver _staffAccess;
    private readonly string[] _allowedRoles;

    public RequireWorkspaceRoleFilter(IWorkspaceClient workspaceClient, IStaffAccessResolver staffAccess, string[] allowedRoles)
    {
        _workspaceClient = workspaceClient;
        _staffAccess = staffAccess;
        _allowedRoles = allowedRoles;
    }

    /// <summary>
    /// G10: what a platform staff member needs to act on a workspace's OWN billing endpoints
    /// (the ones listing SystemAdmin): billing.read to look, billing.subscriptions_manage to
    /// change anything. Before staff roles every "admin" token passed here, which would now mean
    /// a Read-only Auditor could start a checkout for somebody else's workspace.
    /// </summary>
    internal static string StaffOverridePermission(string httpMethod) =>
        HttpMethods.IsGet(httpMethod) || HttpMethods.IsHead(httpMethod)
            ? AdminPermissions.BillingRead
            : AdminPermissions.BillingSubscriptionsManage;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (_allowedRoles.Contains(WorkspaceRoleConstants.SystemAdmin) &&
            await _staffAccess.StaffOverrideAllowsAsync(
                context.HttpContext.User,
                StaffOverridePermission(context.HttpContext.Request.Method),
                context.HttpContext.RequestAborted))
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

        var accessResult = await _workspaceClient.VerifyWorkspaceRolesAsync(workspaceId, userId.Value, _allowedRoles);
        if (!accessResult.IsSuccess)
        {
            context.Result = new ObjectResult(new ApiErrorResponse(
                accessResult.Error ?? ApiMessageConstants.ErrorMessages.BillingInternalError,
                accessResult.ErrorCode ?? ErrorCodes.InternalServerError))
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
            return;
        }

        if (!accessResult.Value)
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
}
