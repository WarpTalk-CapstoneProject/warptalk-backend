using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using WarpTalk.BillingService.API.Authorization;
using WarpTalk.BillingService.API.Controllers;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Tests.API.Controllers;

/// <summary>
/// WT-260, exercised rather than asserted by reflection.
///
/// A real WarpTalk account carries the platform role 'user' in its JWT — that is all the
/// production seed grants. Workspace Owner/Admin is membership data that lives in
/// workspace-service and never appears as a token claim. The old
/// [Authorize(Roles = "Owner, Admin, admin")] on checkout therefore rejected every real user
/// before any filter ran, which is why checkout and top-up 403'd for everyone.
///
/// These tests drive the attribute that replaced it, with a principal that has exactly the
/// claims a demo account has.
/// </summary>
public class PaymentsCheckoutAuthorizationTests
{
    private static readonly Guid UserId = Guid.Parse("8f1c5b30-1c7a-4d2e-9a11-0f7e2c4b6a01");
    private static readonly Guid WorkspaceId = Guid.Parse("b21d4f88-3c66-4f5a-9d0e-77c1a9f43e02");

    /// <summary>A demo account's token: platform role 'user', nothing else.</summary>
    private static ClaimsPrincipal PlatformUserPrincipal() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, UserId.ToString()),
                new Claim(ClaimTypes.Role, "user"),
            ],
            authenticationType: "TestJwt",
            nameType: ClaimTypes.Name,
            roleType: ClaimTypes.Role));

    [Fact]
    public void TheOldJwtRoleCheck_WouldHaveRejected_ARealWorkspaceOwner()
    {
        var principal = PlatformUserPrincipal();

        // This is exactly what [Authorize(Roles = "Owner, Admin, admin")] evaluates, and it is
        // false for every account the production seed creates — however senior they are in the
        // workspace. Kept as a regression anchor for why the attribute had to change.
        Assert.False(principal.IsInRole(WorkspaceRoleConstants.Owner));
        Assert.False(principal.IsInRole(WorkspaceRoleConstants.Admin));
        Assert.False(principal.IsInRole(WorkspaceRoleConstants.SystemAdmin));
    }

    [Fact]
    public async Task CheckoutIsReached_ByAPlatformUserWhoOwnsTheWorkspace()
    {
        var workspaceClient = new Mock<IWorkspaceClient>();
        workspaceClient
            .Setup(client => client.VerifyWorkspaceRolesAsync(
                WorkspaceId, UserId, It.IsAny<string[]>()))
            .ReturnsAsync(Result.Success(true));

        var context = ActionExecutingContextFor(
            nameof(PaymentsController.CreateCheckoutSession),
            PlatformUserPrincipal(),
            new CreateCheckoutSessionRequest(UserId, WorkspaceId, 10m));

        var reachedTheAction = false;

        await RunWorkspaceRoleFilterAsync(
            nameof(PaymentsController.CreateCheckoutSession),
            workspaceClient.Object,
            context,
            () =>
            {
                reachedTheAction = true;
                return Task.FromResult(new ActionExecutedContext(context, [], controller: null!));
            });

        Assert.True(
            reachedTheAction,
            "A workspace Owner whose JWT carries only the platform role 'user' must reach checkout.");
        Assert.Null(context.Result);

        // The filter must ask workspace-service, which is the whole point: the answer is not in
        // the token.
        workspaceClient.Verify(
            client => client.VerifyWorkspaceRolesAsync(
                WorkspaceId,
                UserId,
                It.Is<string[]>(roles =>
                    roles.Contains(WorkspaceRoleConstants.Owner) &&
                    roles.Contains(WorkspaceRoleConstants.Admin))),
            Times.Once);
    }

    [Fact]
    public async Task CheckoutIsRefused_WhenTheCallerHasNoBillingRoleInThatWorkspace()
    {
        var workspaceClient = new Mock<IWorkspaceClient>();
        workspaceClient
            .Setup(client => client.VerifyWorkspaceRolesAsync(
                WorkspaceId, UserId, It.IsAny<string[]>()))
            .ReturnsAsync(Result.Success(false));

        var context = ActionExecutingContextFor(
            nameof(PaymentsController.CreateCheckoutSession),
            PlatformUserPrincipal(),
            new CreateCheckoutSessionRequest(UserId, WorkspaceId, 10m));

        var reachedTheAction = false;

        await RunWorkspaceRoleFilterAsync(
            nameof(PaymentsController.CreateCheckoutSession),
            workspaceClient.Object,
            context,
            () =>
            {
                reachedTheAction = true;
                return Task.FromResult(new ActionExecutedContext(context, [], controller: null!));
            });

        Assert.False(reachedTheAction);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    /// <summary>
    /// Runs the <see cref="RequireWorkspaceRoleAttribute"/> declared on the given action, built
    /// through its own <c>CreateInstance</c> so the test exercises the real filter rather than a
    /// re-implementation of it.
    /// </summary>
    private static async Task RunWorkspaceRoleFilterAsync(
        string actionName,
        IWorkspaceClient workspaceClient,
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        var attribute = typeof(PaymentsController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method => method.Name == actionName)
            .GetCustomAttribute<RequireWorkspaceRoleAttribute>();

        Assert.NotNull(attribute);

        var services = new ServiceCollection();
        services.AddSingleton(workspaceClient);
        using var provider = services.BuildServiceProvider();

        var filter = Assert.IsAssignableFrom<IAsyncActionFilter>(
            attribute!.CreateInstance(provider));

        await filter.OnActionExecutionAsync(context, next);
    }

    private static ActionExecutingContext ActionExecutingContextFor(
        string actionName,
        ClaimsPrincipal principal,
        CreateCheckoutSessionRequest request)
    {
        var httpContext = new DefaultHttpContext { User = principal };
        var actionContext = new ActionContext(
            httpContext,
            new Microsoft.AspNetCore.Routing.RouteData(),
            new ControllerActionDescriptor { ActionName = actionName },
            new ModelStateDictionary());

        return new ActionExecutingContext(
            actionContext,
            [],
            new Dictionary<string, object?> { ["request"] = request },
            controller: null!);
    }
}
