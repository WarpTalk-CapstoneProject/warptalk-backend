using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace WarpTalk.Shared.Authorization;

/// <summary>
/// Reads a service's controllers and reports every admin endpoint and the permission it requires.
///
/// It lives in the shared library, not in a test project, so the one rule is written once and
/// every service's test suite runs it against its own API assembly:
/// <code>Assert.Empty(AdminEndpointPermissionCoverage.Violations(typeof(Program).Assembly));</code>
///
/// An endpoint is an ADMIN endpoint when its route is under <c>api/v1/admin</c>, or it carries a
/// <see cref="RequirePermissionAttribute"/> (admin surfaces that predate the prefix — the plugin
/// catalog, plans, rate cards — are marked that way). The rule for each: exactly one permission
/// between the controller and the action, a code that exists in <see cref="AdminPermissions"/>,
/// no anonymous access, and none of the pre-G10 role gates (the <c>WarpTalkSystemAdmin</c> policy,
/// <c>Roles = "admin"</c>), which would admit every staff member or only Super Admin regardless of
/// the permission matrix.
/// </summary>
public static class AdminEndpointPermissionCoverage
{
    public const string AdminRoutePrefix = "api/v1/admin";

    public sealed record Endpoint(
        string Controller,
        string Action,
        string HttpMethod,
        string Route,
        IReadOnlyList<string> Permissions,
        bool UsesLegacyGate,
        bool AllowsAnonymous)
    {
        public bool IsAdmin =>
            Route.StartsWith(AdminRoutePrefix, StringComparison.OrdinalIgnoreCase)
            || Permissions.Count > 0
            || UsesLegacyGate;

        public override string ToString() => $"{HttpMethod} /{Route} ({Controller}.{Action})";
    }

    public static IReadOnlyList<Endpoint> Scan(Assembly assembly)
    {
        var endpoints = new List<Endpoint>();
        foreach (var controller in ControllerTypes(assembly))
        {
            var controllerRoute = controller.GetCustomAttributes<RouteAttribute>(inherit: true)
                .Select(r => r.Template).FirstOrDefault() ?? string.Empty;
            var controllerName = controller.Name.EndsWith("Controller", StringComparison.Ordinal)
                ? controller.Name[..^"Controller".Length]
                : controller.Name;
            var controllerAuth = controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();
            var controllerAnonymous = controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any();

            foreach (var method in controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                var verbs = method.GetCustomAttributes<HttpMethodAttribute>(inherit: true).ToList();
                if (verbs.Count == 0 || method.IsDefined(typeof(NonActionAttribute)))
                {
                    continue;
                }

                var actionAuth = method.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();
                var allAuth = controllerAuth.Concat(actionAuth).ToList();
                var permissions = allAuth.OfType<RequirePermissionAttribute>().Select(a => a.Permission).ToList();
                var legacy = allAuth.Any(IsLegacyGate);
                var anonymous = controllerAnonymous || method.IsDefined(typeof(AllowAnonymousAttribute), inherit: true);

                foreach (var verb in verbs)
                {
                    var route = Combine(controllerRoute, verb.Template, controllerName);
                    foreach (var httpMethod in verb.HttpMethods)
                    {
                        endpoints.Add(new Endpoint(
                            controller.Name, method.Name, httpMethod, route, permissions, legacy, anonymous));
                    }
                }
            }
        }

        return endpoints;
    }

    public static IReadOnlyList<Endpoint> AdminEndpoints(Assembly assembly) =>
        Scan(assembly).Where(e => e.IsAdmin).ToList();

    /// <summary>Human-readable violations; empty when every admin endpoint maps to exactly one permission.</summary>
    public static IReadOnlyList<string> Violations(Assembly assembly)
    {
        var violations = new List<string>();
        foreach (var endpoint in AdminEndpoints(assembly))
        {
            if (endpoint.Permissions.Count == 0)
                violations.Add($"{endpoint}: admin endpoint without [RequirePermission].");
            else if (endpoint.Permissions.Count > 1)
                violations.Add($"{endpoint}: {endpoint.Permissions.Count} permissions ({string.Join(", ", endpoint.Permissions)}); an admin endpoint maps to exactly one.");

            foreach (var code in endpoint.Permissions.Where(code => !AdminPermissions.IsKnown(code)))
                violations.Add($"{endpoint}: '{code}' is not in AdminPermissions.");

            if (endpoint.UsesLegacyGate)
                violations.Add($"{endpoint}: still gated by the pre-G10 system-admin role/policy; use [RequirePermission].");

            if (endpoint.AllowsAnonymous)
                violations.Add($"{endpoint}: admin endpoint allows anonymous access.");
        }

        return violations;
    }

    private static bool IsLegacyGate(AuthorizeAttribute attribute) =>
        attribute is not RequirePermissionAttribute
        && (string.Equals(attribute.Policy, SystemAdminAuthorization.PolicyName, StringComparison.Ordinal)
            || (attribute.Roles?.Split(',').Select(r => r.Trim())
                .Any(r => r is SystemAdminAuthorization.RoleName or WorkspaceRoleConstants.Admin) ?? false));

    private static IEnumerable<Type> ControllerTypes(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).ToArray()!;
        }

        return types.Where(t =>
            t is { IsClass: true, IsAbstract: false, IsPublic: true }
            && typeof(ControllerBase).IsAssignableFrom(t));
    }

    private static string Combine(string controllerRoute, string? actionTemplate, string controllerName)
    {
        string route;
        if (!string.IsNullOrEmpty(actionTemplate) && (actionTemplate.StartsWith("~/", StringComparison.Ordinal) || actionTemplate.StartsWith('/')))
        {
            route = actionTemplate.TrimStart('~').TrimStart('/');
        }
        else if (string.IsNullOrEmpty(actionTemplate))
        {
            route = controllerRoute;
        }
        else
        {
            route = string.IsNullOrEmpty(controllerRoute) ? actionTemplate : $"{controllerRoute.TrimEnd('/')}/{actionTemplate}";
        }

        return route
            .Replace("[controller]", controllerName, StringComparison.OrdinalIgnoreCase)
            .TrimStart('/');
    }
}
