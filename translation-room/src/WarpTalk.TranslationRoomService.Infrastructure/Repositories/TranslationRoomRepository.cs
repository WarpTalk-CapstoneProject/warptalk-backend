using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;

namespace WarpTalk.TranslationRoomService.Infrastructure.Repositories;

public class TranslationRoomRepository : GenericRepository<TranslationRoom>, ITranslationRoomRepository
{
    public TranslationRoomRepository(TranslationRoomDbContext dbContext) : base(dbContext)
    {
    }

    public async Task<bool> ExistsByCodeAsync(string roomCode, IEnumerable<RoomStatus>? excludedStatuses = null, CancellationToken cancellationToken = default)
    {
        var room = await _dbSet.FirstOrDefaultAsync(r => r.TranslationRoomCode == roomCode, cancellationToken);
        if (room == null) return false;

        if (excludedStatuses != null && excludedStatuses.Contains(room.Status))
            return false;

        return true;
    }

    public async Task<TranslationRoom?> GetByCodeAsync(string roomCode, IEnumerable<RoomStatus>? excludedStatuses = null, CancellationToken cancellationToken = default)
    {
        var room = await _dbSet.FirstOrDefaultAsync(r => r.TranslationRoomCode == roomCode, cancellationToken);
        if (room == null) return null;

        if (excludedStatuses != null && excludedStatuses.Contains(room.Status))
            return null;

        return room;
    }
}
