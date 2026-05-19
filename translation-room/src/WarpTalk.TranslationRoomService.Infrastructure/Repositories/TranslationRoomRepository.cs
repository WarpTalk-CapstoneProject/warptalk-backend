using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;

namespace WarpTalk.TranslationRoomService.Infrastructure.Repositories;

public class TranslationRoomRepository : GenericRepository<TranslationRoom>, ITranslationRoomRepository
{
    public TranslationRoomRepository(TranslationRoomDbContext dbContext) : base(dbContext)
    {
    }

    public async Task<bool> ExistsByCodeAsync(string roomCode, IEnumerable<string>? excludedStatuses = null, CancellationToken cancellationToken = default)
    {
        var query = _dbSet.Where(r => r.TranslationRoomCode == roomCode);

        if (excludedStatuses != null && excludedStatuses.Any())
        {
            var statusList = excludedStatuses.ToList();
            query = query.Where(r => !statusList.Contains(r.Status));
        }

        return await query.AnyAsync(cancellationToken);
    }

    public async Task<TranslationRoom?> GetByCodeAsync(string roomCode, IEnumerable<string>? excludedStatuses = null, CancellationToken cancellationToken = default)
    {
        var query = _dbSet.Where(r => r.TranslationRoomCode == roomCode);

        if (excludedStatuses != null && excludedStatuses.Any())
        {
            var statusList = excludedStatuses.ToList();
            query = query.Where(r => !statusList.Contains(r.Status));
        }

        return await query.FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<List<TranslationRoom>> GetHistoryByUserIdAsync(Guid userId, int limit, int offset, CancellationToken ct = default)
    {
        var terminalStatuses = TranslationRoomConstants.TerminalStatuses;
        
        var query = _dbSet
            .Include(r => r.TranslationRoomParticipants)
            .Include(r => r.TranslationRoomArtifacts)
            .Where(r => terminalStatuses.Contains(r.Status) && r.DeletedAt == null &&
                        (r.HostId == userId || r.TranslationRoomParticipants.Any(p => p.UserId == userId)))
            .OrderByDescending(r => r.CreatedAt)
            .Skip(offset)
            .Take(limit);

        return await query.ToListAsync(ct);
    }
}
