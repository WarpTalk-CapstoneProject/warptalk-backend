using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using WarpTalk.Shared.Authorization;
using WarpTalk.WorkspaceService.API.Controllers;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// The outbox console reads and replays dead letters across every tenant, so it is a platform
/// surface. It was gated on <c>Roles = "Admin"</c> — the workspace-administrator role name, which
/// <c>auth.user_roles</c> also grants globally — and so admitted accounts that administer one
/// workspace to every other workspace's events.
/// </summary>
public class WorkspaceOutboxAdminControllerTests
{
    [Fact]
    public void Controller_IsGatedOnTheSharedSystemAdminPolicy()
    {
        var authorize = typeof(WorkspaceOutboxAdminController)
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .SingleOrDefault();

        Assert.NotNull(authorize);
        Assert.Equal(SystemAdminAuthorization.PolicyName, authorize!.Policy);
        Assert.Null(authorize.Roles);
    }

    [Fact]
    public void NoActionWidensTheGateWithItsOwnRoles()
    {
        var widened = typeof(WorkspaceOutboxAdminController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<AllowAnonymousAttribute>().Any()
                || method.GetCustomAttributes<AuthorizeAttribute>().Any(a => a.Roles is not null))
            .Select(method => method.Name)
            .ToList();

        Assert.Empty(widened);
    }
}
