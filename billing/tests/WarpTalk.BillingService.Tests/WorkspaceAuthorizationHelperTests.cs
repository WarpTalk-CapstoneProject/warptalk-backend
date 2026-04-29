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
    public void ValidateWorkspaceAccess_WhenAuthRequiredAndWorkspaceClaimMissing_ShouldForbid()
    {
        var workspaceId = Guid.NewGuid();
        var context = CreateContext(workspaceId, []);

        var result = WorkspaceAuthorizationHelper.ValidateWorkspaceAccess(context, workspaceId);

        result.Should().BeOfType<ForbidResult>();
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
    public void ValidateServiceAccess_WhenWorkspaceUserAttemptsDeduct_ShouldForbid()
    {
        var workspaceId = Guid.NewGuid();
        var context = CreateContext(workspaceId, [new Claim("workspace_id", workspaceId.ToString())]);

        var result = WorkspaceAuthorizationHelper.ValidateServiceAccess(context, workspaceId);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public void ValidateWorkspaceAdminAccess_WhenWorkspaceAdminMatchesWorkspace_ShouldAllow()
    {
        var workspaceId = Guid.NewGuid();
        var context = CreateContext(
            workspaceId,
            [
                new Claim("workspace_id", workspaceId.ToString()),
                new Claim("role", "workspace_admin")
            ]);

        var result = WorkspaceAuthorizationHelper.ValidateWorkspaceAdminAccess(context, workspaceId);

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
