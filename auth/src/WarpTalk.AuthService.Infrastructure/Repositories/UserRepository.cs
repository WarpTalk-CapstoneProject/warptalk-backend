using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;

namespace WarpTalk.AuthService.Infrastructure.Repositories;

public class UserRepository : GenericRepository<User>, IUserRepository
{
    public UserRepository(AuthDbContext context) : base(context)
    {
    }

    public async Task<bool> ExistsByEmailAsync(string email, CancellationToken ct = default)
    {
        return await _dbSet.AnyAsync(u => u.Email == email, ct);
    }

    public async Task<User?> GetByEmailWithRolesAsync(string email, CancellationToken ct = default)
    {
        return await _dbSet.Include(u => u.UserRoleUsers)
            .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Email == email, ct);
    }

    public async Task<User?> GetByIdWithRolesAsync(Guid id, CancellationToken ct = default)
    {
        return await _dbSet.Include(u => u.UserRoleUsers)
            .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == id, ct);
    }

    public async Task<User?> GetByGoogleIdWithRolesAsync(string googleId, CancellationToken ct = default)
    {
        return await _dbSet.Include(u => u.UserRoleUsers)
            .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.GoogleId == googleId, ct);
    }

    public Task<User?> GetByEmailVerificationTokenHashAsync(string tokenHash, CancellationToken ct = default)
        => _dbSet.FirstOrDefaultAsync(u => u.EmailVerificationTokenHash == tokenHash, ct);

    public Task<User?> GetByPasswordResetTokenHashAsync(string tokenHash, CancellationToken ct = default)
        => _dbSet.FirstOrDefaultAsync(u => u.PasswordResetTokenHash == tokenHash, ct);

    public async Task<(IReadOnlyList<AdminUserDirectoryRow> Items, int Total)> GetDirectoryAsync(
        AdminUserDirectoryFilter filter,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var query = ApplyFilters(_dbSet.AsNoTracking(), filter, now);

        var total = await query.CountAsync(ct);

        // Scalars only, projected into an ANONYMOUS type. A positional record projected here and
        // then ordered by one of its own properties does not translate — EF cannot map a
        // constructor parameter back to the expression it came from, and the whole query fails at
        // runtime. That defect shipped once already in billing's usage-by-member endpoint, which
        // returned 500 on every call it ever served.
        var pageRows = await ApplySort(query, filter.Sort)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.FullName,
                u.AvatarUrl,
                u.IsActive,
                u.IsLocked,
                u.LockedUntil,
                u.EmailVerified,
                u.LastLoginAt,
                u.CreatedAt,
                u.DeletedAt,
            })
            .ToListAsync(ct);

        if (pageRows.Count == 0)
        {
            return (Array.Empty<AdminUserDirectoryRow>(), total);
        }

        var ids = pageRows.Select(u => u.Id).ToList();
        var rolesByUser = await LoadRolesAsync(ids, ct);
        var sessionsByUser = await LoadActiveSessionCountsAsync(ids, now, ct);

        var items = pageRows
            .Select(u => new AdminUserDirectoryRow(
                u.Id,
                u.Email,
                u.FullName,
                u.AvatarUrl,
                u.IsActive,
                u.IsLocked,
                u.LockedUntil,
                u.EmailVerified,
                u.LastLoginAt,
                u.CreatedAt,
                u.DeletedAt,
                sessionsByUser.GetValueOrDefault(u.Id),
                rolesByUser.GetValueOrDefault(u.Id) ?? Array.Empty<string>()))
            .ToList();

        return (items, total);
    }

    public async Task<AdminUserDirectoryRow?> GetDirectoryRowAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var found = await _dbSet
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.FullName,
                u.AvatarUrl,
                u.IsActive,
                u.IsLocked,
                u.LockedUntil,
                u.EmailVerified,
                u.LastLoginAt,
                u.CreatedAt,
                u.DeletedAt,
            })
            .FirstOrDefaultAsync(ct);

        if (found is null) return null;

        var ids = new List<Guid> { found.Id };
        var roles = await LoadRolesAsync(ids, ct);
        var sessions = await LoadActiveSessionCountsAsync(ids, now, ct);

        return new AdminUserDirectoryRow(
            found.Id,
            found.Email,
            found.FullName,
            found.AvatarUrl,
            found.IsActive,
            found.IsLocked,
            found.LockedUntil,
            found.EmailVerified,
            found.LastLoginAt,
            found.CreatedAt,
            found.DeletedAt,
            sessions.GetValueOrDefault(found.Id),
            roles.GetValueOrDefault(found.Id) ?? Array.Empty<string>());
    }

    public async Task<IReadOnlyList<AdminUserSessionRow>> GetActiveSessionsAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return await _context.RefreshTokens
            .AsNoTracking()
            .Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new AdminUserSessionRow(
                t.Id,
                t.DeviceInfo,
                t.IpAddress,
                t.CreatedAt,
                t.ExpiresAt))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Roles per user, read in one grouped query for the whole page rather than per row.
    ///
    /// A revoked assignment is not a role: `user_roles` keeps the row and stamps `revoked_at`, so
    /// filtering on it is what stops the directory from reporting a permission somebody no longer
    /// holds.
    /// </summary>
    private async Task<Dictionary<Guid, IReadOnlyList<string>>> LoadRolesAsync(
        IReadOnlyList<Guid> userIds,
        CancellationToken ct)
    {
        var pairs = await _context.UserRoles
            .AsNoTracking()
            .Where(ur => userIds.Contains(ur.UserId) && ur.RevokedAt == null)
            .Select(ur => new { ur.UserId, RoleName = ur.Role.Name })
            .ToListAsync(ct);

        // OrdinalIgnoreCase, not the default OrderBy. A bare `OrderBy(n => n)` on strings sorts
        // by the SERVER'S CURRENT CULTURE, so the same two roles come back as ["admin", "Member"]
        // on one host and ["Member", "admin"] on another — a response that changes with a locale
        // nobody set deliberately. This codebase has already been bitten by ambient culture once,
        // when it turned 0.006575 into 6575 in a billing payload.
        //
        // IgnoreCase rather than plain Ordinal because auth.roles seeds both 'admin' and 'Admin',
        // and ordinal would file every capitalised role above every lowercase one.
        return pairs
            .GroupBy(p => p.UserId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g
                    .Select(p => p.RoleName)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList());
    }

    /// <summary>Live sessions per user — neither revoked nor expired — counted in the database.</summary>
    private async Task<Dictionary<Guid, int>> LoadActiveSessionCountsAsync(
        IReadOnlyList<Guid> userIds,
        DateTime now,
        CancellationToken ct)
    {
        var counts = await _context.RefreshTokens
            .AsNoTracking()
            .Where(t => userIds.Contains(t.UserId) && t.RevokedAt == null && t.ExpiresAt > now)
            .GroupBy(t => t.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return counts.ToDictionary(c => c.UserId, c => c.Count);
    }

    private static IQueryable<User> ApplyFilters(
        IQueryable<User> query,
        AdminUserDirectoryFilter filter,
        DateTime now)
    {
        // Deleted accounts are EXCLUDED unless asked for by name. They are still rows, and a
        // directory that silently mixes them in reports a headcount nobody has.
        query = filter.Status == "deleted"
            ? query.Where(u => u.DeletedAt != null)
            : query.Where(u => u.DeletedAt == null);

        query = filter.Status switch
        {
            // "Locked" is either flag or an unexpired lockout window — checking only IsLocked
            // misses everyone the failed-login lockout is currently holding.
            "locked" => query.Where(u => u.IsLocked || (u.LockedUntil != null && u.LockedUntil > now)),
            "unverified" => query.Where(u => !u.EmailVerified),
            "deactivated" => query.Where(u => !u.IsActive),
            "active" => query.Where(u =>
                u.IsActive
                && u.EmailVerified
                && !u.IsLocked
                && (u.LockedUntil == null || u.LockedUntil <= now)),
            _ => query,
        };

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            query = query.Where(u =>
                EF.Functions.ILike(u.Email, $"%{term}%")
                || EF.Functions.ILike(u.FullName, $"%{term}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.Role))
        {
            var role = filter.Role.Trim();
            query = query.Where(u =>
                u.UserRoleUsers.Any(ur => ur.RevokedAt == null && ur.Role.Name == role));
        }

        return query;
    }

    private static IQueryable<User> ApplySort(IQueryable<User> query, string sort) => sort switch
    {
        "created_asc" => query.OrderBy(u => u.CreatedAt),
        "name_asc" => query.OrderBy(u => u.FullName),
        "name_desc" => query.OrderByDescending(u => u.FullName),
        // Nulls last on both: an account that has never signed in is not the most recent one, and
        // PostgreSQL sorts NULL highest on DESC by default.
        "last_login_desc" => query
            .OrderByDescending(u => u.LastLoginAt != null)
            .ThenByDescending(u => u.LastLoginAt),
        "last_login_asc" => query
            .OrderByDescending(u => u.LastLoginAt != null)
            .ThenBy(u => u.LastLoginAt),
        _ => query.OrderByDescending(u => u.CreatedAt),
    };
}
