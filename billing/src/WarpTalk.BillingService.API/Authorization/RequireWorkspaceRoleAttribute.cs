using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
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
    private readonly string[] _allowedRoles;

    public RequireWorkspaceRoleFilter(IWorkspaceClient workspaceClient, string[] allowedRoles)
    {
        _workspaceClient = workspaceClient;
        _allowedRoles = allowedRoles;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (_allowedRoles.Contains(WorkspaceRoleConstants.SystemAdmin) &&
            (context.HttpContext.User.IsInRole(WorkspaceRoleConstants.SystemAdmin) ||
             context.HttpContext.User.IsInRole(WorkspaceRoleConstants.Admin)))
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
