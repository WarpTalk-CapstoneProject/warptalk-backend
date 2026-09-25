using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WarpTalk.Shared.Authorization;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// The shared staff authorization every service's admin endpoints use (G10), replacing the
/// single system-admin role gate of WT-205. Lives here alongside the other WarpTalk.Shared tests.
///
/// The property that matters most is that the TOKEN decides nothing: a role claim "admin" is a
/// hint for the UI, and what a caller may do is whatever the auth service answers now.
/// </summary>
public sealed class StaffAuthorizationTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private sealed class CountingSource(Func<Guid, StaffAccess> answer) : IStaffAccessSource
    {
        public int Calls;
        public Exception? Throw;
        public Func<Guid, StaffAccess> Answer { get; set; } = answer;

        public Task<StaffAccess> GetAsync(Guid userId, CancellationToken ct = default)
        {
            Calls++;
            return Throw is null ? Task.FromResult(Answer(userId)) : Task.FromException<StaffAccess>(Throw);
        }
    }

    private static (IAuthorizationService Authorization, IStaffAccessResolver Resolver) Build(IStaffAccessSource source, int cacheSeconds = 30)
    {
        var provider = new ServiceCollection()
            .AddLogging()
            .AddWarpTalkStaffAuthorizationCore()
            .Configure<StaffAuthorizationOptions>(o => o.CacheSeconds = cacheSeconds)
            .AddSingleton(source)
            .BuildServiceProvider();
        return (provider.GetRequiredService<IAuthorizationService>(), provider.GetRequiredService<IStaffAccessResolver>());
    }

    private static ClaimsPrincipal Caller(params string[] roles) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, UserId.ToString()), .. roles.Select(r => new Claim(ClaimTypes.Role, r))],
            "TestAuth", ClaimTypes.NameIdentifier, ClaimTypes.Role));

    private static Task<AuthorizationResult> Check(IAuthorizationService authorization, ClaimsPrincipal user, string permission) =>
        authorization.AuthorizeAsync(user, resource: null, new RequirePermissionAttribute(permission).GetRequirements());

    [Fact]
    public async Task APermissionTheRoleGrants_IsAllowed()
    {
        var (authorization, _) = Build(new DelegateStaffAccessSource(_ => DelegateStaffAccessSource.Staff(AdminPermissions.BillingRead)));

        Assert.True((await Check(authorization, Caller(), AdminPermissions.BillingRead)).Succeeded);
    }

    [Fact]
    public async Task APermissionTheRoleDoesNotGrant_IsRefused()
    {
        var (authorization, _) = Build(new DelegateStaffAccessSource(_ => DelegateStaffAccessSource.Staff(AdminPermissions.BillingRead)));

        Assert.False((await Check(authorization, Caller(), AdminPermissions.BillingAdjustCredit)).Succeeded);
    }

    [Fact]
    public async Task SuperAdmin_HoldsEveryPermission_IncludingOnesItHasNoRowsFor()
    {
        var (authorization, _) = Build(new DelegateStaffAccessSource(_ => DelegateStaffAccessSource.SuperAdmin()));

        foreach (var permission in AdminPermissions.All)
        {
            Assert.True((await Check(authorization, Caller(), permission)).Succeeded, permission);
        }
    }

    [Theory]
    [InlineData("admin")]  // the staff hint the token carries
    [InlineData("Admin")]  // the workspace administrator role
    [InlineData("Owner")]
    public async Task TheTokensRoleClaims_GrantNothing_WhenTheAuthServiceSaysNotStaff(string role)
    {
        // A removed staff member's token still says "admin" until it expires. It must not matter.
        var (authorization, _) = Build(new DelegateStaffAccessSource(_ => StaffAccess.None));

        Assert.False((await Check(authorization, Caller(role), AdminPermissions.WorkspacesRead)).Succeeded);
    }

    [Fact]
    public async Task AnAnonymousCaller_IsRefusedWithoutAskingTheAuthService()
    {
        var source = new CountingSource(_ => DelegateStaffAccessSource.SuperAdmin());
        var (authorization, _) = Build(source);

        var result = await Check(authorization, new ClaimsPrincipal(new ClaimsIdentity()), AdminPermissions.WorkspacesRead);

        Assert.False(result.Succeeded);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task TheLegacySystemAdminPolicy_NowAdmitsOnlySuperAdmin()
    {
        var (asSuper, _) = Build(new DelegateStaffAccessSource(_ => DelegateStaffAccessSource.SuperAdmin()));
        var (asEverythingButSuper, _) = Build(new DelegateStaffAccessSource(_ => DelegateStaffAccessSource.Staff([.. AdminPermissions.All])));

        Assert.True((await asSuper.AuthorizeAsync(Caller("admin"), SystemAdminAuthorization.PolicyName)).Succeeded);
        Assert.False((await asEverythingButSuper.AuthorizeAsync(Caller("admin"), SystemAdminAuthorization.PolicyName)).Succeeded);
    }

    [Fact]
    public async Task TheAnswerIsCached_ThenRevocationLandsWhenTheWindowCloses()
    {
        var source = new CountingSource(_ => DelegateStaffAccessSource.Staff(AdminPermissions.AuditRead));
        var (authorization, resolver) = Build(source, cacheSeconds: 30);

        Assert.True((await Check(authorization, Caller(), AdminPermissions.AuditRead)).Succeeded);
        Assert.True((await Check(authorization, Caller(), AdminPermissions.AuditRead)).Succeeded);
        Assert.Equal(1, source.Calls);

        // The auth service suspends them; its own resolver is invalidated at once.
        source.Answer = _ => StaffAccess.None;
        resolver.Invalidate(UserId);

        Assert.False((await Check(authorization, Caller(), AdminPermissions.AuditRead)).Succeeded);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task InvalidateAll_DropsEveryCachedAnswer_ForARoleEdit()
    {
        var source = new CountingSource(_ => DelegateStaffAccessSource.Staff(AdminPermissions.BillingRead));
        var (authorization, resolver) = Build(source);
        await Check(authorization, Caller(), AdminPermissions.BillingRead);

        source.Answer = _ => DelegateStaffAccessSource.Staff();
        resolver.InvalidateAll();

        Assert.False((await Check(authorization, Caller(), AdminPermissions.BillingRead)).Succeeded);
    }

    [Fact]
    public async Task AnAuthServiceOutage_FailsClosed_AndIsNotCached()
    {
        var source = new CountingSource(_ => DelegateStaffAccessSource.SuperAdmin()) { Throw = new InvalidOperationException("auth down") };
        var (authorization, _) = Build(source);

        Assert.False((await Check(authorization, Caller("admin"), AdminPermissions.HealthRead)).Succeeded);

        source.Throw = null;
        Assert.True((await Check(authorization, Caller("admin"), AdminPermissions.HealthRead)).Succeeded);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task AStaffOverrideOnAnOrdinaryEndpoint_NeedsTheHint_SoOrdinaryUsersNeverCostACall()
    {
        var source = new CountingSource(_ => DelegateStaffAccessSource.SuperAdmin());
        var resolver = new CachedStaffAccessResolver(
            source,
            new MemoryCache(new MemoryCacheOptions()),
            new FixedOptions(new StaffAuthorizationOptions()),
            NullLogger<CachedStaffAccessResolver>.Instance);

        Assert.False(await resolver.StaffOverrideAllowsAsync(Caller("user"), AdminPermissions.BillingRead));
        Assert.Equal(0, source.Calls);

        Assert.True(await resolver.StaffOverrideAllowsAsync(Caller("admin"), AdminPermissions.BillingRead));
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public void AnUnknownPermissionCode_CannotBeDeclared()
    {
        Assert.Throws<ArgumentException>(() => new RequirePermissionAttribute("billing.everything"));
    }

    [Fact]
    public void TheCatalog_HasUniqueCodes_EachInAKnownArea()
    {
        Assert.Equal(AdminPermissions.All.Count, AdminPermissions.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(AdminPermissions.Definitions, d => Assert.Contains(d.Area, AdminPermissions.Areas.Ordered));
    }

    [Fact]
    public void BuiltInRoles_GrantOnlyCatalogPermissions_AndOnlySuperAdminManagesStaff()
    {
        foreach (var role in BuiltInStaffRoles.Definitions)
        {
            Assert.All(role.Permissions, code => Assert.True(AdminPermissions.IsKnown(code), $"{role.Slug}: {code}"));
            if (role.Slug != BuiltInStaffRoles.SuperAdmin)
            {
                Assert.DoesNotContain(AdminPermissions.StaffManage, role.Permissions);
            }
        }
    }

    [Fact]
    public void TheReadOnlyAuditor_CanChangeNothing()
    {
        var auditor = BuiltInStaffRoles.Find(BuiltInStaffRoles.ReadOnlyAuditor)!;
        var writes = auditor.Permissions
            .Where(code => !AdminPermissions.Find(code)!.IsRead && code != AdminPermissions.AuditExport)
            .ToList();

        Assert.Empty(writes);
    }

    private sealed class FixedOptions(StaffAuthorizationOptions value) : IOptionsMonitor<StaffAuthorizationOptions>
    {
        public StaffAuthorizationOptions CurrentValue => value;
        public StaffAuthorizationOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<StaffAuthorizationOptions, string?> listener) => null;
    }
}
