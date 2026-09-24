using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.AssistantService.API.Controllers;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.Shared.Authorization;

namespace WarpTalk.AssistantService.Tests.Plugins;

/// <summary>
/// Pins the two things about the admin catalog surface that are properties of the controller rather
/// than of the service: who may call it, and what path each action claims.
/// </summary>
/// <remarks>
/// Both are the kind of thing an added action silently gets wrong. A new endpoint written without an
/// attribute inherits the class-level policy here, which is why the policy is asserted per action
/// through the resolved-effective-attribute path rather than once on the type - the assertion stays
/// true for actions nobody has written yet, and fails the moment one carries a weaker attribute of
/// its own.
/// </remarks>
public class AssistantPluginCatalogControllerTests
{
    private const string ControllerRoutePrefix = "api/v1/assistant/plugins/catalog";

    private static IEnumerable<MethodInfo> Actions =>
        typeof(AssistantPluginCatalogController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any());

    public static TheoryData<string> ActionNames()
    {
        var data = new TheoryData<string>();
        foreach (var action in Actions) data.Add(action.Name);
        return data;
    }

    /// <summary>
    /// The permission an action requires: its own attribute if it has one, otherwise the
    /// controller's — the same resolution ASP.NET performs.
    /// </summary>
    private static RequirePermissionAttribute? EffectivePermission(MethodInfo action) =>
        action.GetCustomAttribute<RequirePermissionAttribute>()
        ?? action.DeclaringType!.GetCustomAttribute<RequirePermissionAttribute>();

    private static bool IsRead(MethodInfo action) =>
        action.GetCustomAttributes<HttpMethodAttribute>().All(verb => verb.HttpMethods.All(m => m == "GET"));

    [Fact]
    public void TheControllerExposesTheExpectedActions()
    {
        // Guards the theories below: if this ever comes back empty, every per-action assertion
        // would pass vacuously.
        Assert.Equal(
            ["Delete", "Get", "List", "ListAudits", "Rediscover", "ReplaceTools", "SetOAuthClient", "Update"],
            Actions.Select(action => action.Name).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ActionNames))]
    public void EveryActionRequiresThePluginsPermissionForWhatItDoes(string actionName)
    {
        var action = Assert.Single(Actions, method => method.Name == actionName);

        var permission = EffectivePermission(action);

        Assert.NotNull(permission);
        // Reads need plugins.read; anything that writes the catalog every user reads, plugins.manage.
        Assert.Equal(IsRead(action) ? AdminPermissions.PluginsRead : AdminPermissions.PluginsManage, permission!.Permission);
        Assert.Null(action.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    [Theory]
    [MemberData(nameof(ActionNames))]
    public async Task SomeoneTheAuthServiceSaysIsNotStaffIsRefused_WhateverTheirToken(string actionName)
    {
        var action = Assert.Single(Actions, method => method.Name == actionName);

        // The workspace 'Admin', and even the platform 'admin' hint: neither is the answer.
        var caller = Principal(new Claim(ClaimTypes.Role, "Admin"), new Claim(ClaimTypes.Role, SystemAdminAuthorization.RoleName));

        var authorized = await AuthorizationService(_ => StaffAccess.None)
            .AuthorizeAsync(caller, resource: null, EffectivePermission(action)!.GetRequirements());

        Assert.False(authorized.Succeeded);
    }

    [Theory]
    [MemberData(nameof(ActionNames))]
    public async Task StaffHoldingThePermissionAreAllowed_AndOnlyReadersAreRefusedWrites(string actionName)
    {
        var action = Assert.Single(Actions, method => method.Name == actionName);
        var requirements = EffectivePermission(action)!.GetRequirements();

        var manager = await AuthorizationService(_ => DelegateStaffAccessSource.Staff(AdminPermissions.PluginsRead, AdminPermissions.PluginsManage))
            .AuthorizeAsync(Principal(), resource: null, requirements);
        var reader = await AuthorizationService(_ => DelegateStaffAccessSource.Staff(AdminPermissions.PluginsRead))
            .AuthorizeAsync(Principal(), resource: null, requirements);

        Assert.True(manager.Succeeded);
        Assert.Equal(IsRead(action), reader.Succeeded);
    }

    private static ClaimsPrincipal Principal(params Claim[] extra) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), .. extra],
            "TestAuth",
            ClaimTypes.NameIdentifier,
            ClaimTypes.Role));

    [Fact]
    public void EveryRouteSitsUnderTheCatalogPrefix()
    {
        var prefix = typeof(AssistantPluginCatalogController).GetCustomAttribute<RouteAttribute>();

        Assert.NotNull(prefix);
        Assert.Equal(ControllerRoutePrefix, prefix!.Template);
    }

    [Fact]
    public void TheRouteTemplatesAreTheOnesTheUserFacingControllerCannotShadow()
    {
        var templates = Actions
            .SelectMany(action => action.GetCustomAttributes<HttpMethodAttribute>()
                .SelectMany(http => http.HttpMethods.Select(verb => $"{verb} {ControllerRoutePrefix}/{http.Template}".TrimEnd('/'))))
            .OrderBy(template => template, StringComparer.Ordinal)
            .ToList();

        // Written out rather than derived, so a route added or renamed has to be re-read against the
        // user-facing controller's {pluginKey} routes before this test goes green again.
        Assert.Equal(
            [
                "DELETE api/v1/assistant/plugins/catalog/{pluginKey}",
                "GET api/v1/assistant/plugins/catalog",
                "GET api/v1/assistant/plugins/catalog/{pluginKey}",
                "GET api/v1/assistant/plugins/catalog/{pluginKey}/audits",
                "PATCH api/v1/assistant/plugins/catalog/{pluginKey}",
                "POST api/v1/assistant/plugins/catalog/{pluginKey}/rediscover",
                "PUT api/v1/assistant/plugins/catalog/{pluginKey}/oauth",
                "PUT api/v1/assistant/plugins/catalog/{pluginKey}/tools",
            ],
            templates);
    }

    [Fact]
    public void CatalogIsAReservedPluginKey()
    {
        // catalog/{pluginKey} and the user-facing {pluginKey}/connection are both two segments deep
        // under api/v1/assistant/plugins. A literal outranks a parameter, so nothing is ambiguous -
        // this controller simply wins, and a row genuinely keyed 'catalog' would have its own users'
        // connection calls answered here as a 403.
        Assert.Contains("catalog", PluginConstants.ReservedPluginKeys);
        Assert.True(PluginConstants.IsReservedPluginKey("CATALOG"));

        // The reservation that predates this controller, kept: plugins/mcp/oauth/callback shadows
        // plugins/{pluginKey}/oauth/callback the same way.
        Assert.Contains("mcp", PluginConstants.ReservedPluginKeys);
    }

    private static IAuthorizationService AuthorizationService(Func<Guid, StaffAccess> access) =>
        new ServiceCollection()
            .AddLogging()
            .AddWarpTalkStaffAuthorizationCore()
            .AddSingleton<IStaffAccessSource>(new DelegateStaffAccessSource(access))
            .BuildServiceProvider()
            .GetRequiredService<IAuthorizationService>();
}
