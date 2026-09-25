using Microsoft.EntityFrameworkCore;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;

namespace WarpTalk.AuthService.Infrastructure.Repositories;

public class RoleRepository : GenericRepository<Role>, IRoleRepository
{
    public RoleRepository(AuthDbContext context) : base(context)
    {
    }

    public async Task<IReadOnlyList<Role>> ListStaffRolesAsync(CancellationToken ct = default) =>
        await StaffRoles()
            .OrderByDescending(r => r.IsSystem)
            .ThenBy(r => r.Name)
            .ToListAsync(ct);

    public Task<Role?> GetStaffRoleAsync(Guid id, CancellationToken ct = default) =>
        StaffRoles().FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<Role?> GetStaffRoleBySlugAsync(string slug, CancellationToken ct = default) =>
        StaffRoles().FirstOrDefaultAsync(r => r.Slug == slug, ct);

    public Task<bool> NameOrSlugTakenAsync(string name, string slug, Guid? excludingRoleId, CancellationToken ct = default)
    {
        var lowerName = name.ToLower();
        return _dbSet.AnyAsync(r =>
                (excludingRoleId == null || r.Id != excludingRoleId)
                && (r.Name.ToLower() == lowerName || r.Slug == slug),
            ct);
    }

    public void RemovePermissions(IEnumerable<RolePermission> rows) => _context.RolePermissions.RemoveRange(rows);

    private IQueryable<Role> StaffRoles() =>
        _dbSet
            .Include(r => r.RolePermissions)
                .ThenInclude(rp => rp.Permission)
            .Where(r => r.Scope == StaffConstants.RoleScopes.PlatformStaff && r.DeletedAt == null);
}
