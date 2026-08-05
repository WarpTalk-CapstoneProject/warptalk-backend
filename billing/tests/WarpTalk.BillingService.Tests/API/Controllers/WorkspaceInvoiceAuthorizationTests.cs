using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Moq;
using WarpTalk.BillingService.API.Authorization;
using WarpTalk.BillingService.API.Controllers;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Tests.API.Controllers;

/// <summary>
/// WT-260: GET /api/v1/invoices/workspace/{workspaceId} returned 403 with an empty body for a
/// workspace Owner because the action used [Authorize(Roles = ...)] — JWT claim-based
/// authorization — instead of [RequireWorkspaceRole(...)], which resolves per-workspace
/// membership through workspace-service. These tests drive the real filter using the roles
/// declared on the action itself, so they follow the controller rather than a copy of it.
/// </summary>
public class WorkspaceInvoiceAuthorizationTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task GetWorkspaceInvoices_WorkspaceOwner_IsAllowedThrough()
    {
        var workspaceClient = new Mock<IWorkspaceClient>();
        workspaceClient
            .Setup(client => client.VerifyWorkspaceRolesAsync(
                WorkspaceId, UserId, It.IsAny<string[]>()))
            .ReturnsAsync(Result.Success(true));

        var context = CreateActionExecutingContext();
        var filter = CreateFilterForAction(workspaceClient.Object);

        var nextInvoked = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            nextInvoked = true;
            return Task.FromResult(CreateActionExecutedContext(context));
        });

        Assert.True(nextInvoked, "A workspace Owner must reach the invoice action.");
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task GetWorkspaceInvoices_NonMember_IsForbiddenWithErrorBody()
    {
        var workspaceClient = new Mock<IWorkspaceClient>();
        workspaceClient
            .Setup(client => client.VerifyWorkspaceRolesAsync(
                WorkspaceId, UserId, It.IsAny<string[]>()))
            .ReturnsAsync(Result.Success(false));

        var context = CreateActionExecutingContext();
        var filter = CreateFilterForAction(workspaceClient.Object);

        var nextInvoked = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            nextInvoked = true;
            return Task.FromResult(CreateActionExecutedContext(context));
        });

        Assert.False(nextInvoked, "A non-member must not reach the invoice action.");

        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);

        // The production symptom was an *empty* 403 body — the framework default, proving the
        // request never reached this filter. A membership rejection always carries a payload.
        var error = Assert.IsType<ApiErrorResponse>(result.Value);
        Assert.Equal(ErrorCodes.Forbidden, error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Error));
    }

    [Fact]
    public void GetWorkspaceInvoices_AllowsOwnerAdminAndSystemAdmin()
    {
        Assert.Equal(
            new[]
            {
                WorkspaceRoleConstants.Owner,
                WorkspaceRoleConstants.Admin,
                WorkspaceRoleConstants.SystemAdmin
            },
            GetDeclaredRoles());
    }

    private static string[] GetDeclaredRoles()
    {
        var action = typeof(InvoicesController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method => method.Name == nameof(InvoicesController.GetWorkspaceInvoices));

        var attribute = action.GetCustomAttribute<RequireWorkspaceRoleAttribute>();
        Assert.NotNull(attribute);
        Assert.NotNull(attribute!.Arguments);

        return Assert.IsType<string[]>(Assert.Single(attribute.Arguments!));
    }

    private static RequireWorkspaceRoleFilter CreateFilterForAction(IWorkspaceClient workspaceClient) =>
        new(workspaceClient, GetDeclaredRoles());

    private static ActionExecutingContext CreateActionExecutingContext()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, UserId.ToString())],
            authenticationType: "TestAuth");

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity)
        };

        // Mirrors [HttpGet("workspace/{workspaceId}")] on InvoicesController.
        var routeData = new RouteData();
        routeData.Values["workspaceId"] = WorkspaceId.ToString();

        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        return new ActionExecutingContext(
            actionContext,
            [],
            new Dictionary<string, object?>(),
            controller: null!);
    }

    private static ActionExecutedContext CreateActionExecutedContext(ActionExecutingContext context) =>
        new(context, [], controller: null!);
}
