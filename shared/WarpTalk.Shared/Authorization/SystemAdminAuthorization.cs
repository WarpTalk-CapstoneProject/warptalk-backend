using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Net.ClientFactory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using WarpTalk.Shared.Grpc;
using WarpTalk.Shared.Protos;

namespace WarpTalk.Shared.Authorization;

/// <summary>
/// The platform-admin gate (WT-205), now backed by staff roles and permissions (G10).
///
/// Every admin endpoint declares the ONE permission it needs with
/// <see cref="RequirePermissionAttribute"/>. <see cref="PolicyName"/> survives only as the
/// fail-safe for code that has not been converted yet: it admits an active Super Admin and no one
/// else, which is exactly who "system admin" meant before staff roles existed. The endpoint
/// coverage test refuses it on any controller, so it cannot quietly become the way new endpoints
/// are gated again.
/// </summary>
public static class SystemAdminAuthorization
{
    /// <summary>Policy name: active Super Admin only.</summary>
    public const string PolicyName = "WarpTalkSystemAdmin";

    /// <summary>
    /// The role claim value every active staff member's token carries (see
    /// <see cref="StaffClaims.StaffRoleHint"/>). Historically the platform role seeded in
    /// init-db.sql; since G10 it is written from <c>auth.staff_members</c>, not from
    /// <c>auth.user_roles</c>, and it authorizes nothing by itself.
    /// </summary>
    public const string RoleName = "admin";

    /// <summary>
    /// Registers permission-based staff authorization for a service that is NOT the auth service:
    /// the handler, the cached resolver, and the gRPC source that asks the auth service.
    /// </summary>
    public static IServiceCollection AddWarpTalkStaffAuthorization(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddWarpTalkStaffAuthorizationCore(configuration);

        services.AddGrpcClient<UserService.UserServiceClient>(GrpcStaffAccessSource.ClientName, o =>
                o.Address = ResolveAuthServiceUri(configuration, environment))
            .AddWarpTalkGrpcClientDefaults(configuration, environment);
        services.TryAddSingleton<IStaffAccessSource, GrpcStaffAccessSource>();
        return services;
    }

    /// <summary>
    /// The parts every service shares. The auth service calls this directly and registers its own
    /// database-backed <see cref="IStaffAccessSource"/>.
    /// </summary>
    public static IServiceCollection AddWarpTalkStaffAuthorizationCore(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        services.AddMemoryCache();
        var options = services.AddOptions<StaffAuthorizationOptions>();
        if (configuration is not null)
        {
            options.Bind(configuration.GetSection(StaffAuthorizationOptions.SectionName));
        }

        services.TryAddSingleton<IStaffAccessResolver, CachedStaffAccessResolver>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, PermissionAuthorizationHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, SystemAdminHandler>());
        services.AddAuthorizationBuilder()
            .AddPolicy(PolicyName, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.Requirements.Add(new SystemAdminRequirement());
            });
        return services;
    }

    private static Uri ResolveAuthServiceUri(IConfiguration configuration, IHostEnvironment environment)
    {
        // Both spellings exist in the deploy files (GrpcSettings for most services, GrpcUrls for
        // billing and the gateway); the k8s chart sets both for every workload.
        var value = configuration["GrpcSettings:AuthServiceUrl"] ?? configuration["GrpcUrls:AuthServiceUrl"];
        if (!string.IsNullOrWhiteSpace(value))
        {
            return new Uri(value);
        }

        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            return new Uri("http://localhost:50051");
        }

        throw new InvalidOperationException(
            "GrpcSettings:AuthServiceUrl (or GrpcUrls:AuthServiceUrl) is required: admin endpoints ask the auth " +
            "service who is staff, and without an address every admin request would be refused.");
    }
}

public sealed class SystemAdminRequirement : IAuthorizationRequirement;

/// <summary>Grants <see cref="SystemAdminAuthorization.PolicyName"/> to an active Super Admin only.</summary>
public sealed class SystemAdminHandler : AuthorizationHandler<SystemAdminRequirement>
{
    private readonly IStaffAccessResolver _resolver;

    public SystemAdminHandler(IStaffAccessResolver resolver) => _resolver = resolver;

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        SystemAdminRequirement requirement)
    {
        var access = await _resolver.GetAsync(context.User);
        if (access.IsStaff && access.IsSuperAdmin)
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>
/// Declares the one permission an admin endpoint needs:
/// <c>[RequirePermission(AdminPermissions.BillingRead)]</c>.
///
/// An <see cref="AuthorizeAttribute"/> (so it also requires an authenticated caller) that carries
/// its own requirement through <see cref="IAuthorizationRequirementData"/>, so there is no policy
/// per permission to register and no policy name to mistype.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequirePermissionAttribute : AuthorizeAttribute, IAuthorizationRequirementData
{
    public RequirePermissionAttribute(string permission)
    {
        if (!AdminPermissions.IsKnown(permission))
        {
            throw new ArgumentException($"'{permission}' is not in AdminPermissions.", nameof(permission));
        }

        Permission = permission;
    }

    public string Permission { get; }

    public IEnumerable<IAuthorizationRequirement> GetRequirements()
    {
        yield return new PermissionRequirement(Permission);
    }
}

public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IStaffAccessResolver _resolver;

    public PermissionAuthorizationHandler(IStaffAccessResolver resolver) => _resolver = resolver;

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        if (await _resolver.HasPermissionAsync(context.User, requirement.Permission))
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>Asks the auth service (UserService.GetStaffAccess) over the internal gRPC channel.</summary>
public sealed class GrpcStaffAccessSource : IStaffAccessSource
{
    /// <summary>
    /// A named client of its own, so registering it never collides with a service's existing
    /// UserServiceClient registration (and never doubles that client's interceptors).
    /// </summary>
    public const string ClientName = "warptalk-staff-access";

    private readonly GrpcClientFactory _factory;

    public GrpcStaffAccessSource(GrpcClientFactory factory) => _factory = factory;

    public async Task<StaffAccess> GetAsync(Guid userId, System.Threading.CancellationToken ct = default)
    {
        var client = _factory.CreateClient<UserService.UserServiceClient>(ClientName);
        var response = await client.GetStaffAccessAsync(
            new GetUserRequest { Id = userId.ToString() },
            deadline: DateTime.UtcNow.AddSeconds(5),
            cancellationToken: ct);

        return response.IsStaff
            ? new StaffAccess(
                true,
                response.RoleSlug,
                response.RoleName,
                response.IsSuperAdmin,
                response.Permissions.Where(AdminPermissions.IsKnown).ToHashSet(StringComparer.Ordinal))
            : StaffAccess.None;
    }
}
