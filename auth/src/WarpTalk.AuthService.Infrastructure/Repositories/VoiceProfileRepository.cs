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

public class VoiceProfileRepository : GenericRepository<VoiceProfile>, IVoiceProfileRepository
{
    public VoiceProfileRepository(AuthDbContext context) : base(context)
    {
    }

    public async Task<IReadOnlyList<VoiceProfile>> GetByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        return await _dbSet
            .Where(p => p.UserId == userId && p.DeletedAt == null)
            .Include(p => p.VoiceSamples.Where(s => s.DeletedAt == null))
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<VoiceProfile?> GetByIdForUserAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        return await _dbSet
            .Include(p => p.VoiceSamples.Where(s => s.DeletedAt == null))
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId && p.DeletedAt == null, ct);
    }

    public async Task<VoiceProfile?> GetAutoCloneAsync(
        Guid userId, string language, CancellationToken ct = default)
    {
        return await _dbSet.FirstOrDefaultAsync(
            p => p.UserId == userId
                && p.Language == language
                && p.Source == VoiceProfileSources.InMeeting
                && p.DeletedAt == null,
            ct);
    }

    public async Task<IReadOnlyList<VoiceProfile>> GetAutoClonesAsync(
        Guid userId, CancellationToken ct = default)
    {
        return await _dbSet
            .Where(p => p.UserId == userId
                && p.Source == VoiceProfileSources.InMeeting
                && p.DeletedAt == null)
            // Unscored rows sort last: HasValue descending puts true (scored) first, so a
            // measured voice always beats an unmeasured one without inventing a number for the
            // unmeasured one. Ordered in SQL rather than in memory — an in-memory sort over this
            // table is what WT-495 had to take back out.
            .OrderByDescending(p => p.QualityScore.HasValue)
            .ThenByDescending(p => p.QualityScore)
            .ThenByDescending(p => p.UpdatedAt)
            .ToListAsync(ct);
    }

    public void Add(VoiceProfile entity)
    {
        _dbSet.Add(entity);
    }
}
