using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;

namespace WarpTalk.AuthService.Infrastructure.Repositories;

public class RefreshTokenRepository : GenericRepository<RefreshToken>, IRefreshTokenRepository
{
    public RefreshTokenRepository(AuthDbContext context) : base(context)
    {
    }

    public async Task<RefreshToken?> GetByTokenHashAsync(string tokenHash, CancellationToken ct = default)
    {
        return await _dbSet.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);
    }

    public async Task RevokeFamilyAsync(Guid familyId, CancellationToken ct = default)
    {
        await _dbSet
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RevokedAt, DateTime.UtcNow), ct);
    }

    public Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
        => _dbSet
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(t => t.RevokedAt, DateTime.UtcNow),
                ct);

    public async Task<IReadOnlyList<UserSessionRow>> GetActiveSessionsForUserAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // Two plain queries composed in memory rather than one grouped projection: the result is
        // a handful of rows per user, and a positional-record projection EF cannot translate is
        // exactly how an admin aggregation shipped returning 500 on every call.
        var leaves = await _dbSet
            .AsNoTracking()
            .Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now)
            .Select(t => new { t.FamilyId, t.DeviceInfo, t.IpAddress, t.CreatedAt, t.ExpiresAt })
            .ToListAsync(ct);

        if (leaves.Count == 0) return Array.Empty<UserSessionRow>();

        var familyIds = leaves.Select(l => l.FamilyId).Distinct().ToList();
        var signedIn = await _dbSet
            .AsNoTracking()
            .Where(t => t.UserId == userId && familyIds.Contains(t.FamilyId))
            .GroupBy(t => t.FamilyId)
            .Select(g => new { FamilyId = g.Key, SignedInAt = g.Min(t => t.CreatedAt) })
            .ToDictionaryAsync(x => x.FamilyId, x => x.SignedInAt, ct);

        // Normally one live leaf per family; a refresh race can briefly leave two. The session is
        // still one session, described by its newest leaf.
        return leaves
            .GroupBy(l => l.FamilyId)
            .Select(g => g.OrderByDescending(l => l.CreatedAt).First())
            .Select(l => new UserSessionRow(
                l.FamilyId,
                l.DeviceInfo,
                l.IpAddress,
                signedIn.TryGetValue(l.FamilyId, out var first) ? first : l.CreatedAt,
                l.CreatedAt,
                l.ExpiresAt))
            .OrderByDescending(r => r.LastActiveAt)
            .ToList();
    }

    public Task RevokeAllForUserExceptFamilyAsync(
        Guid userId,
        Guid keepFamilyId,
        CancellationToken ct = default)
        => _dbSet
            .Where(t => t.UserId == userId && t.FamilyId != keepFamilyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(t => t.RevokedAt, DateTime.UtcNow),
                ct);
}
