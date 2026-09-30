using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Domain.Interfaces;

public interface IRoleRepository : IGenericRepository<Role>
{
    /// <summary>Every staff role with its permissions loaded, built-in first.</summary>
    Task<IReadOnlyList<Role>> ListStaffRolesAsync(CancellationToken ct = default);

    /// <summary>A staff role (not a legacy one) with its permissions loaded; null when absent or deleted.</summary>
    Task<Role?> GetStaffRoleAsync(Guid id, CancellationToken ct = default);

    Task<Role?> GetStaffRoleBySlugAsync(string slug, CancellationToken ct = default);

    /// <summary>Whether any role — staff or legacy — already uses this name or slug.</summary>
    Task<bool> NameOrSlugTakenAsync(string name, string slug, Guid? excludingRoleId, CancellationToken ct = default);

    /// <summary>Marks role-permission rows for deletion (auth.role_permissions has no repository of its own).</summary>
    void RemovePermissions(IEnumerable<RolePermission> rows);
}
