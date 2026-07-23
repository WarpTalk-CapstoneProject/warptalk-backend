using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.BillingService.API.Filters;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class WorkspaceAuthorizeAttribute : Attribute, IAsyncActionFilter
{
    public string Roles { get; set; } = "Owner, Admin";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var httpContext = context.HttpContext;
        var user = httpContext.User;

        var userId = user.GetUserId();
        if (userId == null)
        {
            context.Result = new UnauthorizedObjectResult(new ApiErrorResponse("Authentication required", "UNAUTHORIZED"));
            return;
        }

        Guid? workspaceId = null;

        // 1. Try route parameter
        if (context.RouteData.Values.TryGetValue("workspaceId", out var routeWorkspaceIdObj) &&
            routeWorkspaceIdObj != null &&
            Guid.TryParse(routeWorkspaceIdObj.ToString(), out var parsedRouteId))
        {
            workspaceId = parsedRouteId;
        }
        else
        {
            // 2. Try action arguments for a WorkspaceId property
            var requestWithWorkspaceId = context.ActionArguments.Values
                .FirstOrDefault(arg => arg != null && 
                    (arg.GetType().GetProperty("WorkspaceId") != null || arg.GetType().GetProperty("workspaceId") != null));

            if (requestWithWorkspaceId != null)
            {
                var prop = requestWithWorkspaceId.GetType().GetProperty("WorkspaceId") ?? 
                           requestWithWorkspaceId.GetType().GetProperty("workspaceId");
                
                var val = prop?.GetValue(requestWithWorkspaceId);
                if (val is Guid guidVal)
                {
                    workspaceId = guidVal;
                }
                else if (val is string strVal && Guid.TryParse(strVal, out var parsedGuid))
                {
                    workspaceId = parsedGuid;
                }
            }

            // 3. Try action arguments for a SubscriptionId property and query WorkspaceId
            if (workspaceId == null)
            {
                var requestWithSubscriptionId = context.ActionArguments.Values
                    .FirstOrDefault(arg => arg != null && 
                        (arg.GetType().GetProperty("SubscriptionId") != null || arg.GetType().GetProperty("subscriptionId") != null));

                if (requestWithSubscriptionId != null)
                {
                    var prop = requestWithSubscriptionId.GetType().GetProperty("SubscriptionId") ?? 
                               requestWithSubscriptionId.GetType().GetProperty("subscriptionId");
                    
                    var val = prop?.GetValue(requestWithSubscriptionId);
                    Guid? subscriptionId = null;
                    if (val is Guid guidVal)
                    {
                        subscriptionId = guidVal;
                    }
                    else if (val is string strVal && Guid.TryParse(strVal, out var parsedGuid))
                    {
                        subscriptionId = parsedGuid;
                    }

                    if (subscriptionId.HasValue)
                    {
                        var unitOfWork = httpContext.RequestServices.GetService<IUnitOfWork>();
                        if (unitOfWork != null)
                        {
                            var sub = await unitOfWork.SubscriptionRepository.GetByIdAsync(subscriptionId.Value, httpContext.RequestAborted);
                            if (sub == null)
                            {
                                context.Result = new NotFoundObjectResult(new ApiErrorResponse("Subscription not found.", "NOT_FOUND"));
                                return;
                            }
                            workspaceId = sub.WorkspaceId;
                        }
                    }
                }
            }
        }

        if (workspaceId == null)
        {
            context.Result = new BadRequestObjectResult(new ApiErrorResponse("Workspace ID is required in the route or request body.", "INVALID_REQUEST"));
            return;
        }

        var authService = httpContext.RequestServices.GetService<IWorkspaceAuthorizationService>();
        if (authService == null)
        {
            context.Result = new ObjectResult(new ApiErrorResponse("Workspace authorization service not configured.", "INTERNAL_ERROR"))
            {
                StatusCode = 500
            };
            return;
        }

        var authResult = await authService.AuthorizeAsync(workspaceId.Value, userId.Value, Roles, httpContext.RequestAborted);

        if (!authResult.IsSuccess)
        {
            var statusCode = authResult.ErrorCode == "FORBIDDEN" ? 403 : 500;
            context.Result = new ObjectResult(new ApiErrorResponse(authResult.Error ?? "Access denied.", authResult.ErrorCode ?? "FORBIDDEN"))
            {
                StatusCode = statusCode
            };
            return;
        }

        await next();
    }
}
