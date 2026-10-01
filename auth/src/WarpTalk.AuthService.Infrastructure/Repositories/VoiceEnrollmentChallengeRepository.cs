using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;

namespace WarpTalk.AuthService.Infrastructure.Repositories;

public class VoiceEnrollmentChallengeRepository
    : GenericRepository<VoiceEnrollmentChallenge>, IVoiceEnrollmentChallengeRepository
{
    public VoiceEnrollmentChallengeRepository(AuthDbContext context) : base(context)
    {
    }

    public async Task<VoiceEnrollmentChallenge?> GetForUserAsync(
        Guid id, Guid userId, CancellationToken ct = default)
    {
        return await _dbSet.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);
    }

    public async Task<bool> TryConsumeAsync(
        Guid id, Guid userId, DateTime now, CancellationToken ct = default)
    {
        var claimed = await _dbSet
            .Where(c => c.Id == id
                && c.UserId == userId
                && c.ConsumedAt == null
                && c.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.ConsumedAt, (DateTime?)now), ct);
        return claimed == 1;
    }

    public async Task<int> CountIssuedSinceAsync(
        Guid userId, DateTime since, CancellationToken ct = default)
    {
        return await _dbSet.CountAsync(c => c.UserId == userId && c.CreatedAt >= since, ct);
    }
}
