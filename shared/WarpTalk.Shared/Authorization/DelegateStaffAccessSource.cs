using System;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.Shared.Authorization;

/// <summary>
/// An <see cref="IStaffAccessSource"/> that answers from a function. For test hosts, which need
/// "this caller is a Support agent" without a running auth service. No service registers it.
/// </summary>
public sealed class DelegateStaffAccessSource : IStaffAccessSource
{
    private readonly Func<Guid, StaffAccess> _answer;

    public DelegateStaffAccessSource(Func<Guid, StaffAccess> answer) => _answer = answer;

    public Task<StaffAccess> GetAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(_answer(userId));

    /// <summary>Active staff holding exactly these permissions.</summary>
    public static StaffAccess Staff(params string[] permissions) =>
        new(true, "custom_test", "Test role", false, new System.Collections.Generic.HashSet<string>(permissions, StringComparer.Ordinal));

    /// <summary>An active Super Admin.</summary>
    public static StaffAccess SuperAdmin() =>
        new(true, BuiltInStaffRoles.SuperAdmin, "Super Admin", true, new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal));
}

public static class StaffAccessTestHostExtensions
{
    /// <summary>
    /// FOR INTEGRATION TEST HOSTS ONLY. Replaces the auth service's answer with the pre-G10 rule:
    /// a caller whose token carries the "admin" hint is a Super Admin, anyone else is not staff.
    /// Lets the existing end-to-end suites keep exercising admin endpoints through the real
    /// [RequirePermission] pipeline without a running auth service. The cache is turned off so a
    /// test that switches the caller's token is never answered from a previous test's entry.
    /// </summary>
    public static IServiceCollection UseTokenHintAsStaffAccessForTests(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.RemoveAll<IStaffAccessSource>();
        services.AddSingleton<IStaffAccessSource>(provider => new DelegateStaffAccessSource(_ =>
        {
            var user = provider.GetRequiredService<IHttpContextAccessor>().HttpContext?.User;
            return user is not null && StaffClaims.CarriesStaffHint(user)
                ? DelegateStaffAccessSource.SuperAdmin()
                : StaffAccess.None;
        }));
        services.Configure<StaffAuthorizationOptions>(options => options.CacheSeconds = 0);
        return services;
    }
}
