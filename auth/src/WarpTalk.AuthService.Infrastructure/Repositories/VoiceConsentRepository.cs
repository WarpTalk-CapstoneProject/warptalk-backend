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

public class VoiceConsentRepository : GenericRepository<VoiceConsent>, IVoiceConsentRepository
{
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

    public void Add(VoiceConsent entity)
    {
        _dbSet.Add(entity);
    }
}
