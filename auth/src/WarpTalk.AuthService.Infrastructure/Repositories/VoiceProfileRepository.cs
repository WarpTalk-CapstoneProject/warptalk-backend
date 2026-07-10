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

    public async Task<IReadOnlyList<VoiceProfile>> GetByUserIdAsync(Guid userId, Guid? workspaceId = null, CancellationToken ct = default)
    {
        var query = _dbSet
            .AsNoTracking()
            .Include(p => p.Samples.Where(s => s.DeletedAt == null))
            .Include(p => p.Consents)
            .Where(p => p.UserId == userId && p.DeletedAt == null && p.IsActive);

        if (workspaceId.HasValue)
        {
            query = query.Where(p => p.WorkspaceId == workspaceId.Value);
        }

        return await query
            .OrderByDescending(p => p.UpdatedAt)
            .ToListAsync(ct);
    }

    public async Task<VoiceProfile?> GetByIdForUserAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        return await _dbSet
            .Include(p => p.Samples.Where(s => s.DeletedAt == null))
            .Include(p => p.Consents)
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId && p.DeletedAt == null, ct);
    }
}
