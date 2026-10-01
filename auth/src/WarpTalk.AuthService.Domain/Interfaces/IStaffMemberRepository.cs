using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Domain.Interfaces;

/// <summary>A staff member with the account fields the portal shows beside it.</summary>
public sealed record StaffMemberRow(
    StaffMember Member,
    string Email,
    string FullName,
    string? AvatarUrl,
    DateTime? LastLoginAt,
    bool AccountActive);

public interface IStaffMemberRepository : IGenericRepository<StaffMember>
{
    /// <summary>The row with its role (and the role's permissions) loaded.</summary>
    Task<StaffMember?> GetByUserIdAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<StaffMemberRow>> ListWithAccountsAsync(CancellationToken ct = default);

    Task<StaffMemberRow?> GetWithAccountAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Active holders of the given role slug, optionally ignoring one person.</summary>
    Task<int> CountActiveWithRoleSlugAsync(string slug, Guid? excludingUserId, CancellationToken ct = default);

    Task<int> CountWithRoleAsync(Guid roleId, CancellationToken ct = default);

    /// <summary>Whether the account holds an unrevoked legacy 'admin' row in auth.user_roles.</summary>
    Task<bool> HoldsLegacyAdminRoleAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Deletes the account's legacy 'admin' auth.user_roles row, if any. Called whenever a person
    /// stops being an active Super Admin, so a rollback to pre-G10 code cannot hand them back the
    /// access this system took away.
    /// </summary>
    Task<int> RemoveLegacyAdminRoleAsync(Guid userId, CancellationToken ct = default);
}
