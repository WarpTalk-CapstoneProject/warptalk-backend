using System.Reflection;
using Microsoft.AspNetCore.Mvc.Routing;
using WarpTalk.NotificationService.API.Controllers;
using WarpTalk.Shared.AdminAudit;
using WarpTalk.Shared.Events;

namespace WarpTalk.NotificationService.Tests.API.Controllers;

/// <summary>
/// Every admin CMS write goes into the platform audit log — through [AdminAudited], which records
/// the change (with its diff) before it commits and records a refused request as failed. A new
/// write endpoint without the attribute is the silent gap this guards.
/// </summary>
public sealed class CmsAdminAuditContractTests
{
    private static readonly Type[] CmsControllers =
        [typeof(AdminAnnouncementsController), typeof(AdminEmailTemplatesController), typeof(AdminEmailBlocksController), typeof(AdminEmailSendsController)];

    public static TheoryData<string> WriteEndpoints()
    {
        var data = new TheoryData<string>();
        foreach (var controller in CmsControllers)
        foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var verbs = method.GetCustomAttributes<HttpMethodAttribute>().SelectMany(a => a.HttpMethods).ToList();
            if (verbs.Count == 0 || verbs.All(v => v == "GET")) continue;
            // Previews render a draft and an estimate counts an audience; neither writes anything.
            if (method.Name is "Preview" or "Estimate") continue;
            data.Add($"{controller.Name}.{method.Name}");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(WriteEndpoints))]
    public void EveryCmsWrite_IsAuditedWithAKnownActionAndEntityType(string endpoint)
    {
        var (controllerName, methodName) = (endpoint.Split('.')[0], endpoint.Split('.')[1]);
        var method = CmsControllers.Single(c => c.Name == controllerName).GetMethod(methodName)!;

        var audited = method.GetCustomAttribute<AdminAuditedAttribute>();

        Assert.NotNull(audited);
        Assert.Contains(audited!.Action, AdminAuditCmsActions.All);
        Assert.Contains(audited.EntityType, AdminAuditEntityTypes.All);
        Assert.NotEmpty(audited.SubjectTypes);
    }

    [Fact]
    public void AudienceSends_NeedTheirOwnPermission_NotTheEditingOne()
    {
        var required = typeof(AdminEmailSendsController).GetCustomAttribute<WarpTalk.Shared.Authorization.RequirePermissionAttribute>();
        Assert.Equal(WarpTalk.Shared.Authorization.AdminPermissions.ContentEmailSend, required?.Permission);
    }

    [Fact]
    public void EveryCmsAuditAction_FitsTheAuditColumn()
    {
        Assert.All(AdminAuditCmsActions.All, action => Assert.True(action.Length <= 30, action));
        Assert.Equal(AdminAuditCmsActions.All.Length, AdminAuditCmsActions.All.Distinct().Count());
    }
}
