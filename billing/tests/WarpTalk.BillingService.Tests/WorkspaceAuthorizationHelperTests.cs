using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.BillingService.API.Security;

namespace WarpTalk.BillingService.Tests;

public class WorkspaceAuthorizationHelperTests
{
    [Fact]
    public void ValidateWorkspaceAccess_WhenAuthRequiredAndWorkspaceClaimMissing_ShouldAllow()
    {
        // Missing workspace claim is allowed - it's treated as a cross-workspace admin operation
        var workspaceId = Guid.NewGuid();
        var context = CreateContext(workspaceId, []);

        var result = WorkspaceAuthorizationHelper.ValidateWorkspaceAccess(context, workspaceId);

        result.Should().BeNull();
    }

    [Fact]
    public void ValidateWorkspaceAccess_WhenWorkspaceClaimMatches_ShouldAllow()
    {
        var workspaceId = Guid.NewGuid();
        var context = CreateContext(workspaceId, [new Claim("workspace_id", workspaceId.ToString())]);

        var result = WorkspaceAuthorizationHelper.ValidateWorkspaceAccess(context, workspaceId);

        result.Should().BeNull();
    }

    [Fact]
    public void ValidateWorkspaceAccess_WhenAdminRoleHasNoWorkspaceClaim_ShouldAllow()
    {
        var workspaceId = Guid.NewGuid();
        var context = CreateContext(workspaceId, [new Claim("role", "admin")]);

        var result = WorkspaceAuthorizationHelper.ValidateWorkspaceAccess(context, workspaceId);

        result.Should().BeNull();
    }

    [Fact]
    public void ValidateWorkspaceAccess_WhenUserFromDifferentWorkspace_ShouldForbid()
    {
        var workspaceA = Guid.NewGuid();
        var workspaceB = Guid.NewGuid();
        var context = CreateContext(workspaceA, [new Claim("workspace_id", workspaceA.ToString())]);

        var result = WorkspaceAuthorizationHelper.ValidateWorkspaceAccess(context, workspaceB);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public void ValidateWorkspaceAccess_WhenAdminRole_ShouldAllowAnyWorkspace()
    {
        var workspaceA = Guid.NewGuid();
        var workspaceB = Guid.NewGuid();
        var context = CreateContext(
            workspaceA,
            [
                new Claim("workspace_id", workspaceA.ToString()),
                new Claim(ClaimTypes.Role, "admin")
            ]);

        var result = WorkspaceAuthorizationHelper.ValidateWorkspaceAccess(context, workspaceB);

        result.Should().BeNull();
    }

    private static DefaultHttpContext CreateContext(Guid workspaceId, Claim[] claims)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:RequireAuthentication"] = "true"
            })
            .Build();

        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
        };

        context.Request.Headers["X-Workspace-Id"] = workspaceId.ToString();
        return context;
    }
}
