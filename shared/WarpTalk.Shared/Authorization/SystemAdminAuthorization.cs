using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace WarpTalk.Shared.Authorization;

/// <summary>
/// The single gate for every <c>~/api/v1/admin/*</c> endpoint across all services (WT-205).
/// </summary>
public static class SystemAdminAuthorization
{
    /// <summary>Policy name. Use <c>[Authorize(Policy = SystemAdminAuthorization.PolicyName)]</c>.</summary>
    public const string PolicyName = "WarpTalkSystemAdmin";

    /// <summary>
    /// The platform-wide system-administrator role seeded in init-db.sql. Lowercase, and the
    /// case matters — see <see cref="SystemAdminHandler"/>.
    /// </summary>
    public const string RoleName = "admin";

    /// <summary>
    /// Registers the system-admin policy. Safe to call alongside <c>AddAuthorization()</c>.
    /// </summary>
    public static IServiceCollection AddWarpTalkSystemAdminAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationHandler, SystemAdminHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(PolicyName, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.Requirements.Add(new SystemAdminRequirement());
            });
        return services;
    }
}

public sealed class SystemAdminRequirement : IAuthorizationRequirement;

/// <summary>
/// Grants the policy only to a caller holding the exact role value <c>"admin"</c>.
///
/// <c>auth.roles</c> seeds both <c>'admin'</c> (platform system administrator) and <c>'Admin'</c>
/// (workspace administrator), so the exactness matters. <c>[Authorize(Roles = "admin")]</c> is
/// also exact here — <c>ClaimsIdentity.IsInRole</c> compares the claim value with
/// <c>StringComparison.Ordinal</c>, only the claim type is case-insensitive — so this handler is
/// not fixing a case-sensitivity hole. What it buys instead:
///
/// <list type="bullet">
/// <item>one named gate the services share, so "what protects an admin endpoint" has a single
/// answer and a single test suite rather than a role string copy-pasted per controller;</item>
/// <item>a place to add further requirements (step-up auth, IP allow-lists, break-glass audit)
/// without touching every controller;</item>
/// <item>tolerance for tokens whose role claims arrive under the short <c>role</c>/<c>roles</c>
/// types when inbound claim mapping is disabled, which role-based authorization would miss.</item>
/// </list>
///
/// The exact-match behaviour is pinned by SystemAdminAuthorizationTests so a framework change
/// cannot silently widen it.
/// </summary>
public sealed class SystemAdminHandler : AuthorizationHandler<SystemAdminRequirement>
{
    // Short claim types a JWT can carry when inbound claim mapping is disabled.
    private static readonly string[] FallbackRoleClaimTypes = ["role", "roles"];

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        SystemAdminRequirement requirement)
    {
        if (context.User.Identities.Any(HoldsSystemAdminRole))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    private static bool HoldsSystemAdminRole(ClaimsIdentity identity) =>
        identity.IsAuthenticated
        && identity.Claims.Any(claim =>
            IsRoleClaimType(identity, claim.Type)
            && string.Equals(claim.Value, SystemAdminAuthorization.RoleName, StringComparison.Ordinal));

    private static bool IsRoleClaimType(ClaimsIdentity identity, string claimType) =>
        string.Equals(claimType, identity.RoleClaimType, StringComparison.Ordinal)
        || FallbackRoleClaimTypes.Contains(claimType, StringComparer.Ordinal);
}
