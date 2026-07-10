using Microsoft.EntityFrameworkCore;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;

namespace WarpTalk.AuthService.Infrastructure.Repositories;

public class VoiceSampleRepository : GenericRepository<VoiceSample>, IVoiceSampleRepository
{
    public VoiceSampleRepository(AuthDbContext context) : base(context)
    {
    }

    public async Task<IReadOnlyList<VoiceSample>> GetByVoiceProfileIdAsync(Guid voiceProfileId, CancellationToken ct = default)
    {
        return await _dbSet
            .AsNoTracking()
            .Where(s => s.VoiceProfileId == voiceProfileId && s.DeletedAt == null)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
    }
}
