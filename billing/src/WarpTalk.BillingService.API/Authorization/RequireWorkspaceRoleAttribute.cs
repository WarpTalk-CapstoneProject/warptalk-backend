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
    private const string WorkspaceIdRouteKey = "workspaceId";

    private readonly IWorkspaceClient _workspaceClient;
    private readonly string[] _allowedRoles;

    public RequireWorkspaceRoleFilter(IWorkspaceClient workspaceClient, string[] allowedRoles)
    {
        _workspaceClient = workspaceClient;
        _allowedRoles = allowedRoles;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var userId = context.HttpContext.User.GetUserId();
        if (userId == null)
        {
            context.Result = new UnauthorizedObjectResult(new ApiErrorResponse(
                ApiMessageConstants.ErrorMessages.UnauthorizedTokenDetail,
                ErrorCodes.Unauthorized));
            return;
        }

        if (!TryGetWorkspaceId(context, out var workspaceId))
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

    private static bool TryGetWorkspaceId(ActionExecutingContext context, out Guid workspaceId)
    {
        if (TryParseWorkspaceId(context.RouteData.Values[WorkspaceIdRouteKey], out workspaceId))
        {
            return true;
        }

        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null)
            {
                continue;
            }

            if (argument is Guid id && id != Guid.Empty)
            {
                workspaceId = id;
                return true;
            }

            if (argument is IWorkspaceScopedRequest scopedRequest &&
                TryParseWorkspaceId(scopedRequest.WorkspaceId, out workspaceId))
            {
                return true;
            }
        }

        workspaceId = Guid.Empty;
        return false;
    }

    private static bool TryParseWorkspaceId(object? value, out Guid workspaceId)
    {
        workspaceId = Guid.Empty;

        return value switch
        {
            Guid id when id != Guid.Empty => (workspaceId = id) != Guid.Empty,
            string text when Guid.TryParse(text, out var id) && id != Guid.Empty => (workspaceId = id) != Guid.Empty,
            _ => false
        };
    }
}
