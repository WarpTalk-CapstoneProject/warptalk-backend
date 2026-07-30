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

    public void Add(VoiceProfile entity)
    {
        _dbSet.Add(entity);
    }
}
