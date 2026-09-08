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
    /// The effective authorization attribute for an action: its own if it has one, otherwise the
    /// controller's. This is the same resolution ASP.NET performs.
    /// </summary>
    private static AuthorizeAttribute? EffectiveAuthorize(MethodInfo action) =>
        action.GetCustomAttribute<AuthorizeAttribute>()
        ?? action.DeclaringType!.GetCustomAttribute<AuthorizeAttribute>();

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
    public void EveryActionIsGatedOnTheSystemAdminPolicy(string actionName)
    {
        var action = Assert.Single(Actions, method => method.Name == actionName);

        var authorize = EffectiveAuthorize(action);

        Assert.NotNull(authorize);
        Assert.Equal(SystemAdminAuthorization.PolicyName, authorize!.Policy);
        // No action may opt out: this controller writes the catalog every user reads.
        Assert.Null(action.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    [Theory]
    [MemberData(nameof(ActionNames))]
    public async Task ANonAdminIsRefusedByTheGateEveryActionSitsBehind(string actionName)
    {
        var action = Assert.Single(Actions, method => method.Name == actionName);
        var policyName = EffectiveAuthorize(action)!.Policy!;

        // A workspace 'Admin' rather than the platform 'admin' - the seeded role most likely to be
        // mistaken for one that should pass here. ASP.NET turns a failed policy on an authenticated
        // caller into a 403.
        var workspaceAdmin = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "Admin")],
            "TestAuth",
            ClaimTypes.NameIdentifier,
            ClaimTypes.Role));

        var authorized = await AuthorizationService().AuthorizeAsync(workspaceAdmin, resource: null, policyName);

        Assert.False(authorized.Succeeded);
    }

    [Theory]
    [MemberData(nameof(ActionNames))]
    public async Task APlatformAdminIsAllowedThrough(string actionName)
    {
        var action = Assert.Single(Actions, method => method.Name == actionName);
        var policyName = EffectiveAuthorize(action)!.Policy!;

        var systemAdmin = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Role, SystemAdminAuthorization.RoleName),
            ],
            "TestAuth",
            ClaimTypes.NameIdentifier,
            ClaimTypes.Role));

        var authorized = await AuthorizationService().AuthorizeAsync(systemAdmin, resource: null, policyName);

        Assert.True(authorized.Succeeded);
    }

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

    private static IAuthorizationService AuthorizationService() =>
        new ServiceCollection()
            .AddLogging()
            .AddWarpTalkSystemAdminAuthorization()
            .BuildServiceProvider()
            .GetRequiredService<IAuthorizationService>();
}
