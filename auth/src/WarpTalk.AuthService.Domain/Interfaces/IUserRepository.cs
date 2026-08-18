using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Domain.Interfaces;

/// <summary>
/// What the platform-admin user directory is being asked for. All members are optional; an empty
/// filter lists every account.
/// </summary>
/// <param name="Search">Matched against email and full name, case-insensitively.</param>
/// <param name="Status">
/// all | active | locked | unverified | deactivated | deleted. Anything else is the caller's
/// mistake and is rejected before it reaches SQL.
/// </param>
/// <param name="Role">A platform role name — the same values auth.roles seeds.</param>
public sealed record AdminUserDirectoryFilter(
    string? Search = null,
    string? Status = null,
    string? Role = null,
    string Sort = "created_desc");

/// <summary>
/// One account as the directory lists it.
///
/// <paramref name="ActiveSessionCount"/> counts refresh tokens that are neither revoked nor
/// expired — "signed in somewhere right now", which is the only session fact an administrator can
/// act on. <paramref name="Roles"/> holds PLATFORM roles only; workspace membership lives in
/// another service and is resolved separately for the detail view.
/// </summary>
public sealed record AdminUserDirectoryRow(
    Guid Id,
    string Email,
    string FullName,
    string? AvatarUrl,
    bool IsActive,
    bool IsLocked,
    DateTime? LockedUntil,
    bool EmailVerified,
    DateTime? LastLoginAt,
    DateTime CreatedAt,
    DateTime? DeletedAt,
    int ActiveSessionCount,
    IReadOnlyList<string> Roles);

/// <summary>One signed-in session, as much of it as is safe to show. Never the token itself.</summary>
public sealed record AdminUserSessionRow(
    Guid Id,
    string? DeviceInfo,
    string? IpAddress,
    DateTime CreatedAt,
    DateTime ExpiresAt);

public interface IUserRepository : IGenericRepository<User>
{
    Task<bool> ExistsByEmailAsync(string email, CancellationToken ct = default);
    Task<User?> GetByEmailWithRolesAsync(string email, CancellationToken ct = default);
    Task<User?> GetByIdWithRolesAsync(Guid id, CancellationToken ct = default);
    Task<User?> GetByGoogleIdWithRolesAsync(string googleId, CancellationToken ct = default);
    Task<User?> GetByEmailVerificationTokenHashAsync(string tokenHash, CancellationToken ct = default);
    Task<User?> GetByPasswordResetTokenHashAsync(string tokenHash, CancellationToken ct = default);

    /// <summary>
    /// One page of the platform user directory, plus the total the filter matches.
    ///
    /// A dedicated method rather than an exposed <c>IQueryable</c>: this repository's
    /// <see cref="IGenericRepository{T}"/> has no <c>Query()</c>, and adding one would let any
    /// caller compose a query the repository cannot test.
    /// </summary>
    Task<(IReadOnlyList<AdminUserDirectoryRow> Items, int Total)> GetDirectoryAsync(
        AdminUserDirectoryFilter filter,
        int page,
        int pageSize,
        CancellationToken ct = default);

    /// <summary>One account for the detail view, or null. Same shape as a directory row.</summary>
    Task<AdminUserDirectoryRow?> GetDirectoryRowAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Sessions that are live right now — not revoked, not expired — newest first.</summary>
    Task<IReadOnlyList<AdminUserSessionRow>> GetActiveSessionsAsync(
        Guid userId,
        CancellationToken ct = default);
}
