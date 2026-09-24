using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using WarpTalk.Shared.Authorization;
using WarpTalk.WorkspaceService.API.Controllers;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests;

/// <summary>
/// The admin workspace page's workspace-service actions are system-admin gated server-side by the
/// platform policy, and no action opens a hole in it.
/// </summary>
public class AdminWorkspaceActionsControllerSecurityTests
{
    [Fact]
    public void Each_action_requires_its_own_staff_permission()
    {
        // G10: notes and notices are Support's; handing a workspace to someone else is not.
        static string PermissionOf(string action) => typeof(AdminWorkspaceActionsController).GetMethod(action)!
            .GetCustomAttribute<RequirePermissionAttribute>()!.Permission;

        Assert.Equal(AdminPermissions.WorkspacesLifecycle, PermissionOf(nameof(AdminWorkspaceActionsController.TransferOwnership)));
        Assert.Equal(AdminPermissions.WorkspacesWrite, PermissionOf(nameof(AdminWorkspaceActionsController.SendNotice)));
        Assert.Equal(AdminPermissions.WorkspacesWrite, PermissionOf(nameof(AdminWorkspaceActionsController.AddNote)));
        Assert.Equal(AdminPermissions.WorkspacesWrite, PermissionOf(nameof(AdminWorkspaceActionsController.Export)));
        Assert.Equal(AdminPermissions.WorkspacesRead, PermissionOf(nameof(AdminWorkspaceActionsController.GetTimeline)));
        Assert.Null(typeof(AdminWorkspaceActionsController).GetCustomAttribute<AuthorizeAttribute>());
    }

    [Theory]
    [InlineData(nameof(AdminWorkspaceActionsController.TransferOwnership), "transfer-ownership")]
    [InlineData(nameof(AdminWorkspaceActionsController.SendNotice), "notices")]
    [InlineData(nameof(AdminWorkspaceActionsController.AddNote), "notes")]
    [InlineData(nameof(AdminWorkspaceActionsController.GetTimeline), "timeline")]
    [InlineData(nameof(AdminWorkspaceActionsController.Export), "export")]
    public void Each_action_has_its_route_and_no_anonymous_door(string actionName, string template)
    {
        var action = typeof(AdminWorkspaceActionsController).GetMethod(actionName)!;

        Assert.Equal(template, action.GetCustomAttributes<HttpMethodAttribute>().Single().Template);
        Assert.Null(action.GetCustomAttribute<AllowAnonymousAttribute>());
    }
}
