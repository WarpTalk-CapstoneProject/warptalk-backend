using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;

namespace WarpTalk.AuthService.Infrastructure.Repositories;

public class VoiceConsentRepository : GenericRepository<VoiceConsent>, IVoiceConsentRepository
{
    private const string GrantedStatus = VoiceConsentStatuses.Granted;

    public VoiceConsentRepository(AuthDbContext context) : base(context)
    {
    }

    public async Task<VoiceConsent?> GetCurrentAsync(
        Guid userId,
        string consentType,
        CancellationToken ct = default)
    {
        return await _dbSet
            .Where(c => c.UserId == userId && c.ConsentType == consentType)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<VoiceConsent>> GetHistoryAsync(
        Guid userId,
        string consentType,
        CancellationToken ct = default)
    {
        return await _dbSet
            .Where(c => c.UserId == userId && c.ConsentType == consentType)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<AdminVoiceConsentSnapshot> GetAdminSnapshotAsync(CancellationToken ct = default)
    {
        var all = _dbSet.AsNoTracking();

        // The newest row per (user, consent type), expressed as "no other row for this pair is
        // newer". NOT EXISTS translates cleanly and is exact; GroupBy(...).Select(g => g.OrderBy
        // (...).First()) is the obvious spelling and does not translate on this provider.
        //
        // Id breaks a tie on created_at. The ids are uuidv7, so the larger one is the later
        // insert — two decisions in the same millisecond still order the way they happened,
        // rather than picking whichever row the plan happened to reach first.
        var current = all.Where(c => !all.Any(other =>
            other.UserId == c.UserId
            && other.ConsentType == c.ConsentType
            && (other.CreatedAt > c.CreatedAt
                || (other.CreatedAt == c.CreatedAt && other.Id > c.Id))));

        var byStatus = await current
            .GroupBy(c => new { c.ConsentType, c.ConsentStatus })
            .Select(g => new
            {
                g.Key.ConsentType,
                g.Key.ConsentStatus,
                People = g.Count(),
            })
            .ToListAsync(ct);

        var byVersion = await current
            .Where(c => c.ConsentStatus == GrantedStatus)
            .GroupBy(c => c.ConsentTextVersion)
            .Select(g => new { TextVersion = g.Key, People = g.Count() })
            .ToListAsync(ct);

        var totalDecisions = await all.CountAsync(ct);

        return new AdminVoiceConsentSnapshot(
            byStatus
                .Select(r => new AdminVoiceConsentStatusCount(r.ConsentType, r.ConsentStatus, r.People))
                .OrderBy(r => r.ConsentType, StringComparer.Ordinal)
                .ThenBy(r => r.Status, StringComparer.Ordinal)
                .ToList(),
            byVersion
                .Select(r => new AdminVoiceConsentVersionCount(r.TextVersion, r.People))
                // Newest wording first: the version strings are date-prefixed, so this is
                // chronological, and the interesting row is how many are still on an older one.
                .OrderByDescending(r => r.TextVersion, StringComparer.Ordinal)
                .ToList(),
            totalDecisions);
    }

    public void Add(VoiceConsent entity)
    {
        _dbSet.Add(entity);
    }
}
