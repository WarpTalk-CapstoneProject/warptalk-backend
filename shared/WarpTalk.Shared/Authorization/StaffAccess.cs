using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpTalk.Shared.Extensions;

namespace WarpTalk.Shared.Authorization;

/// <summary>
/// What a person may do in the platform-admin portal, as the auth service knows it right now.
/// </summary>
/// <param name="IsStaff">
/// An ACTIVE staff member. A suspended one is not staff for authorization purposes — the row
/// exists, the access does not.
/// </param>
public sealed record StaffAccess(
    bool IsStaff,
    string? RoleSlug,
    string? RoleName,
    bool IsSuperAdmin,
    IReadOnlySet<string> Permissions)
{
    public static readonly StaffAccess None =
        new(false, null, null, false, new HashSet<string>(StringComparer.Ordinal));

    /// <summary>Super Admin holds every permission by rule, including ones added after it was seeded.</summary>
    public bool Has(string permission) =>
        IsStaff && (IsSuperAdmin || Permissions.Contains(permission));

    /// <summary>The effective list the web renders from: every catalog code for a Super Admin.</summary>
    public IReadOnlyList<string> EffectivePermissions() =>
        !IsStaff ? [] : IsSuperAdmin ? AdminPermissions.All : AdminPermissions.All.Where(Permissions.Contains).ToArray();
}

/// <summary>
/// Where staff access comes from. The auth service answers from its own database; every other
/// service asks the auth service over gRPC (<see cref="GrpcStaffAccessSource"/>).
///
/// Implementations THROW when they cannot answer. "Could not ask" must never be confused with
/// "is not staff" by the source; the resolver decides what an outage means (deny, uncached).
/// </summary>
public interface IStaffAccessSource
{
    Task<StaffAccess> GetAsync(Guid userId, CancellationToken ct = default);
}

public interface IStaffAccessResolver
{
    Task<StaffAccess> GetAsync(Guid userId, CancellationToken ct = default);

    /// <summary>The caller's access, or <see cref="StaffAccess.None"/> for a token with no subject.</summary>
    Task<StaffAccess> GetAsync(ClaimsPrincipal user, CancellationToken ct = default);

    Task<bool> HasPermissionAsync(ClaimsPrincipal user, string permission, CancellationToken ct = default);

    /// <summary>Drop one person's cached answer (the auth service calls this after a staff write).</summary>
    void Invalidate(Guid userId);

    /// <summary>Drop every cached answer — a role's permissions changed, so every holder moved.</summary>
    void InvalidateAll();
}

public sealed class StaffAuthorizationOptions
{
    public const string SectionName = "StaffAuthorization";

    /// <summary>
    /// How long one service trusts an answer. This is the revocation bound: a suspended or removed
    /// staff member, or a role whose permissions were cut, is refused by every service within this
    /// many seconds — independent of how long their access token has left to live.
    /// </summary>
    public int CacheSeconds { get; set; } = 30;
}

/// <summary>
/// Per-process cache in front of <see cref="IStaffAccessSource"/>.
///
/// WHY NOT PUT THE PERMISSIONS IN THE JWT. An access token lives 30 minutes and cannot be recalled.
/// Permissions in it would mean a removed staff member keeps every power until it expires, and a
/// role edit reaches its holders only as each one happens to refresh. Asked here, with a short
/// cache, removal and role edits land within <see cref="StaffAuthorizationOptions.CacheSeconds"/>
/// on every service, the token stays small, and the auth database stays the only source of truth.
/// The price is one gRPC call per person per service per cache window, paid only on admin
/// requests, and admin requests failing closed while the auth service is down — which is the
/// right way round for this surface.
/// </summary>
public sealed class CachedStaffAccessResolver : IStaffAccessResolver
{
    private readonly IStaffAccessSource _source;
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<StaffAuthorizationOptions> _options;
    private readonly ILogger<CachedStaffAccessResolver> _logger;
    private long _generation;

    public CachedStaffAccessResolver(
        IStaffAccessSource source,
        IMemoryCache cache,
        IOptionsMonitor<StaffAuthorizationOptions> options,
        ILogger<CachedStaffAccessResolver> logger)
    {
        _source = source;
        _cache = cache;
        _options = options;
        _logger = logger;
    }

    public async Task<StaffAccess> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var key = CacheKey(userId);
        if (_cache.TryGetValue(key, out StaffAccess? cached) && cached is not null)
        {
            return cached;
        }

        StaffAccess access;
        try
        {
            access = await _source.GetAsync(userId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail closed and do NOT cache: the next request asks again, so a blip costs one
            // refused admin call, not a window of them.
            _logger.LogError(ex, "Staff access for {UserId} could not be resolved; refusing.", userId);
            return StaffAccess.None;
        }

        var seconds = Math.Max(0, _options.CurrentValue.CacheSeconds);
        if (seconds > 0)
        {
            _cache.Set(key, access, TimeSpan.FromSeconds(seconds));
        }

        return access;
    }

    public Task<StaffAccess> GetAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        var userId = user.Identity?.IsAuthenticated == true ? user.GetUserId() : null;
        return userId is null ? Task.FromResult(StaffAccess.None) : GetAsync(userId.Value, ct);
    }

    public async Task<bool> HasPermissionAsync(ClaimsPrincipal user, string permission, CancellationToken ct = default) =>
        (await GetAsync(user, ct)).Has(permission);

    public void Invalidate(Guid userId) => _cache.Remove(CacheKey(userId));

    public void InvalidateAll() => Interlocked.Increment(ref _generation);

    private string CacheKey(Guid userId) =>
        $"staff-access:{Interlocked.Read(ref _generation)}:{userId:N}";
}

/// <summary>Claims the auth service puts in the access token for staff. Hints for the UI, never authority.</summary>
public static class StaffClaims
{
    /// <summary>
    /// Every ACTIVE staff member carries the role claim <c>admin</c> — the value the web and older
    /// code already read as "platform administrator". It says "show this person the portal".
    /// What they may do there is <see cref="IStaffAccessResolver"/>'s answer, per request.
    /// </summary>
    public const string StaffRoleHint = SystemAdminAuthorization.RoleName;

    /// <summary>The staff role's slug (<c>super_admin</c>, <c>support</c>, a custom role's slug).</summary>
    public const string StaffRole = "staff_role";

    /// <summary>Whether the token claims to belong to active staff (the hint, not the answer).</summary>
    public static bool CarriesStaffHint(ClaimsPrincipal user) =>
        user.Identities.Any(identity => identity.IsAuthenticated
            && identity.Claims.Any(claim =>
                (claim.Type == identity.RoleClaimType || claim.Type is "role" or "roles" or ClaimTypes.Role)
                && string.Equals(claim.Value, StaffRoleHint, StringComparison.Ordinal)));
}

public static class StaffAccessResolverExtensions
{
    /// <summary>
    /// For a staff override on a NON-admin endpoint (a workspace's own billing page read by
    /// support, a workspace fetched by id). Those endpoints serve every ordinary user, so the
    /// resolver is asked only when the token carries the staff hint — an ordinary user never costs
    /// a gRPC call. The hint alone grants nothing: the resolver still has the last word, so a
    /// removed staff member's leftover hint is refused within the cache window.
    /// </summary>
    public static async Task<bool> StaffOverrideAllowsAsync(
        this IStaffAccessResolver resolver,
        ClaimsPrincipal user,
        string permission,
        CancellationToken ct = default) =>
        StaffClaims.CarriesStaffHint(user) && await resolver.HasPermissionAsync(user, permission, ct);
}
