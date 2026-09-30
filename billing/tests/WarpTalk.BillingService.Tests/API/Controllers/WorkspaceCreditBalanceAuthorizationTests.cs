using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using WarpTalk.BillingService.API.Authorization;
using WarpTalk.BillingService.API.Controllers;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;

namespace WarpTalk.BillingService.Tests.API.Controllers;

/// <summary>
/// WT-700: GET /api/v1/credits/workspace/{workspaceId} is readable by every active INTERNAL
/// member, and refused to EXTERNAL guests. These tests run the filter built from the attribute
/// declared on <see cref="CreditsController.GetWorkspaceCredits"/>, so putting the Owner/Admin
/// role gate back on the action fails them rather than leaving them green against a copy.
/// </summary>
public class WorkspaceCreditBalanceAuthorizationTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Theory]
    [InlineData("INTERNAL")]
    [InlineData("Internal")]
    public async Task ActiveInternalMember_WithAPlainMemberRole_ReachesTheBalance(string membershipType)
    {
        var workspaceClient = ClientReturning(isMember: true, roleName: "Member", isActive: true, membershipType);

        var (reached, context) = await RunFilterAsync(workspaceClient.Object, PlatformUserPrincipal());

        Assert.True(reached, "An internal workspace member must be able to read the credit balance.");
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task ExternalMember_IsForbidden()
    {
        var workspaceClient = ClientReturning(isMember: true, roleName: "Member", isActive: true, "EXTERNAL");

        var (reached, context) = await RunFilterAsync(workspaceClient.Object, PlatformUserPrincipal());

        Assert.False(reached, "An external guest must not read the workspace's credit balance.");
        AssertForbidden(context);
    }

    [Fact]
    public async Task NonMember_IsForbidden()
    {
        var workspaceClient = ClientReturning(isMember: false, roleName: string.Empty, isActive: false, string.Empty);

        var (reached, context) = await RunFilterAsync(workspaceClient.Object, PlatformUserPrincipal());

        Assert.False(reached);
        AssertForbidden(context);
    }

    [Fact]
    public async Task InactiveInternalMember_IsForbidden()
    {
        var workspaceClient = ClientReturning(isMember: true, roleName: "Member", isActive: false, "INTERNAL");

        var (reached, context) = await RunFilterAsync(workspaceClient.Object, PlatformUserPrincipal());

        Assert.False(reached, "A suspended or removed member must not read the balance.");
        AssertForbidden(context);
    }

    /// <summary>
    /// Fail closed: a membership response that does not say INTERNAL is not internal, even for an
    /// active Owner — the gate is about which organisation the caller belongs to, not their role.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("PARTNER")]
    public async Task UnknownOrEmptyMembershipType_IsForbidden(string membershipType)
    {
        var workspaceClient = ClientReturning(isMember: true, roleName: WorkspaceRoleConstants.Owner, isActive: true, membershipType);

        var (reached, context) = await RunFilterAsync(workspaceClient.Object, PlatformUserPrincipal());

        Assert.False(reached);
        AssertForbidden(context);
    }

    [Fact]
    public async Task WorkspaceServiceFailure_Returns500()
    {
        var workspaceClient = new Mock<IWorkspaceClient>();
        workspaceClient
            .Setup(client => client.GetWorkspaceMemberDetailsAsync(WorkspaceId, UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<(bool, string, bool, string)>("workspace-service unavailable", ErrorCodes.InternalServerError));

        var (reached, context) = await RunFilterAsync(workspaceClient.Object, PlatformUserPrincipal());

        Assert.False(reached);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
    }

    [Fact]
    public async Task StaffWithBillingRead_ReachTheBalance_WithoutAWorkspaceLookup()
    {
        var workspaceClient = new Mock<IWorkspaceClient>(MockBehavior.Strict);

        var (reached, context) = await RunFilterAsync(
            workspaceClient.Object,
            PlatformPrincipal(WorkspaceRoleConstants.SystemAdmin),
            DelegateStaffAccessSource.Staff(AdminPermissions.BillingRead));

        Assert.True(reached, "Staff who may read billing keep access to any workspace's balance.");
        Assert.Null(context.Result);
    }

    /// <summary>
    /// G10: the token's "admin" hint is not the answer. A staff member without billing.read — or
    /// one removed a minute ago whose token still says "admin" — goes through the ordinary
    /// membership check like anyone else. And the WORKSPACE role "Admin" never bypassed anything
    /// by rights; before G10 it did here.
    /// </summary>
    [Theory]
    [InlineData(WorkspaceRoleConstants.SystemAdmin)]
    [InlineData(WorkspaceRoleConstants.Admin)]
    public async Task ARoleClaimWithoutTheStaffPermission_GetsNoBypass(string platformRole)
    {
        var workspaceClient = ClientReturning(isMember: false, roleName: "", isActive: false, membershipType: "");

        var (reached, context) = await RunFilterAsync(
            workspaceClient.Object,
            PlatformPrincipal(platformRole),
            DelegateStaffAccessSource.Staff(AdminPermissions.AuditRead));

        Assert.False(reached);
        AssertForbidden(context);
    }

    [Fact]
    public async Task MissingUserId_IsUnauthorized()
    {
        var workspaceClient = new Mock<IWorkspaceClient>(MockBehavior.Strict);
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var (reached, context) = await RunFilterAsync(workspaceClient.Object, anonymous);

        Assert.False(reached);
        Assert.IsType<UnauthorizedObjectResult>(context.Result);
    }

    private static Mock<IWorkspaceClient> ClientReturning(
        bool isMember, string roleName, bool isActive, string membershipType)
    {
        var workspaceClient = new Mock<IWorkspaceClient>();
        workspaceClient
            .Setup(client => client.GetWorkspaceMemberDetailsAsync(WorkspaceId, UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success((isMember, roleName, isActive, membershipType)));
        return workspaceClient;
    }

    private static void AssertForbidden(ActionExecutingContext context)
    {
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        var error = Assert.IsType<ApiErrorResponse>(result.Value);
        Assert.Equal(ErrorCodes.Forbidden, error.Code);
    }

    /// <summary>A normal account's token: platform role 'user', nothing else.</summary>
    private static ClaimsPrincipal PlatformUserPrincipal() => PlatformPrincipal("user");

    private static ClaimsPrincipal PlatformPrincipal(string platformRole) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, UserId.ToString()),
                new Claim(ClaimTypes.Role, platformRole),
            ],
            authenticationType: "TestJwt",
            nameType: ClaimTypes.Name,
            roleType: ClaimTypes.Role));

    private static async Task<(bool Reached, ActionExecutingContext Context)> RunFilterAsync(
        IWorkspaceClient workspaceClient,
        ClaimsPrincipal principal,
        StaffAccess? staff = null)
    {
        var attribute = typeof(CreditsController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method => method.Name == nameof(CreditsController.GetWorkspaceCredits))
            .GetCustomAttribute<RequireInternalWorkspaceMemberAttribute>();

        Assert.NotNull(attribute);

        var services = new ServiceCollection();
        services.AddSingleton(workspaceClient);
        services.AddLogging();
        services.AddWarpTalkStaffAuthorizationCore();
        services.AddSingleton<IStaffAccessSource>(new DelegateStaffAccessSource(_ => staff ?? StaffAccess.None));
        using var provider = services.BuildServiceProvider();

        var filter = Assert.IsAssignableFrom<IAsyncActionFilter>(attribute!.CreateInstance(provider));

        // Mirrors [HttpGet("workspace/{workspaceId}")] on CreditsController.
        var routeData = new RouteData();
        routeData.Values["workspaceId"] = WorkspaceId.ToString();

        // GET, as on the real route: a staff override on a read needs billing.read, not a write permission.
        var httpContext = new DefaultHttpContext { User = principal };
        httpContext.Request.Method = HttpMethods.Get;

        var context = new ActionExecutingContext(
            new ActionContext(httpContext, routeData, new ActionDescriptor()),
            [],
            new Dictionary<string, object?>(),
            controller: null!);

        var reached = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            reached = true;
            return Task.FromResult(new ActionExecutedContext(context, [], controller: null!));
        });

        return (reached, context);
    }
}
