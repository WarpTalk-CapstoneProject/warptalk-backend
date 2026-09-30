using Microsoft.EntityFrameworkCore;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;

namespace WarpTalk.AuthService.Infrastructure.Repositories;

public class StaffMemberRepository : GenericRepository<StaffMember>, IStaffMemberRepository
{
    public StaffMemberRepository(AuthDbContext context) : base(context)
    {
    }

    public Task<StaffMember?> GetByUserIdAsync(Guid userId, CancellationToken ct = default) =>
        _dbSet
            .Include(m => m.Role)
                .ThenInclude(r => r.RolePermissions)
                    .ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(m => m.UserId == userId, ct);

    public async Task<IReadOnlyList<StaffMemberRow>> ListWithAccountsAsync(CancellationToken ct = default) =>
        await RowsQuery().ToListAsync(ct);

    public Task<StaffMemberRow?> GetWithAccountAsync(Guid userId, CancellationToken ct = default) =>
        RowsQuery(userId).FirstOrDefaultAsync(ct);

    public Task<int> CountActiveWithRoleSlugAsync(string slug, Guid? excludingUserId, CancellationToken ct = default) =>
        _dbSet.CountAsync(m =>
                m.Status == StaffConstants.Statuses.Active
                && m.Role.Slug == slug
                && (excludingUserId == null || m.UserId != excludingUserId)
                // A staff row over a deleted or deactivated account is no one who can sign in.
                && _context.Users.Any(u => u.Id == m.UserId && u.DeletedAt == null && u.IsActive),
            ct);

    public Task<int> CountWithRoleAsync(Guid roleId, CancellationToken ct = default) =>
        _dbSet.CountAsync(m => m.RoleId == roleId, ct);

    public Task<bool> HoldsLegacyAdminRoleAsync(Guid userId, CancellationToken ct = default) =>
        _context.UserRoles.AnyAsync(ur =>
                ur.UserId == userId
                && ur.RevokedAt == null
                && ur.Role.Name == StaffConstants.LegacyAdminRoleName
                && ur.Role.Scope == StaffConstants.RoleScopes.Legacy,
            ct);

    public async Task<int> RemoveLegacyAdminRoleAsync(Guid userId, CancellationToken ct = default)
    {
        var rows = await _context.UserRoles
            .Where(ur => ur.UserId == userId
                && ur.Role.Name == StaffConstants.LegacyAdminRoleName
                && ur.Role.Scope == StaffConstants.RoleScopes.Legacy)
            .ToListAsync(ct);
        _context.UserRoles.RemoveRange(rows);
        return rows.Count;
    }

    /// <summary>
    /// Filters BEFORE the projection, always: EF cannot translate a Where or OrderBy over a
    /// positional-record projection (it compiles, then fails at run time), so callers never get to
    /// filter the rows this returns in SQL.
    /// </summary>
    private IQueryable<StaffMemberRow> RowsQuery(Guid? userId = null) =>
        from member in _dbSet.Include(m => m.Role)
        join user in _context.Users on member.UserId equals user.Id
        where user.DeletedAt == null && (userId == null || member.UserId == userId)
        select new StaffMemberRow(member, user.Email, user.FullName, user.AvatarUrl, user.LastLoginAt, user.IsActive);
}
