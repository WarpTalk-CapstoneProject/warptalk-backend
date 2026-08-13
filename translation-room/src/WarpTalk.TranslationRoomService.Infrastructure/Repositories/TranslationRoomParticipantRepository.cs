using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;

namespace WarpTalk.TranslationRoomService.Infrastructure.Repositories;

public class TranslationRoomParticipantRepository : GenericRepository<TranslationRoomParticipant>, ITranslationRoomParticipantRepository
{
    public TranslationRoomParticipantRepository(TranslationRoomDbContext dbContext) : base(dbContext)
    {
    }

    public async Task<TranslationRoomParticipant?> GetByRoomAndUserAsync(Guid roomId, Guid userId, CancellationToken cancellationToken = default)
    {
        return await _dbSet.FirstOrDefaultAsync(p => p.TranslationRoomId == roomId && p.UserId == userId, cancellationToken);
    }

    public async Task<List<TranslationRoomParticipant>> GetByRoomIdAsync(Guid roomId, CancellationToken ct = default)
    {
        return await _dbSet.Where(p => p.TranslationRoomId == roomId).ToListAsync(ct);
    }

    public async Task<int> CountSeatHoldingParticipantsAsync(Guid roomId, CancellationToken ct = default)
    {
        return await _dbSet.CountAsync(
            p => p.TranslationRoomId == roomId &&
                 TranslationRoomParticipantStatuses.SeatHolding.Contains(p.Status),
            ct);
    }

    public async Task<Dictionary<Guid, int>> CountSeatHoldingParticipantsByRoomsAsync(
        IReadOnlyCollection<Guid> roomIds,
        CancellationToken ct = default)
    {
        if (roomIds.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        var ids = roomIds as List<Guid> ?? roomIds.ToList();

        // SeatHolding.Contains — the array form — because this predicate is translated to SQL.
        // TranslationRoomParticipantStatuses.HoldsSeat(...) is a method and would force the whole
        // roster to be materialised, which is the failure this method exists to avoid.
        return await _dbSet
            .Where(p => ids.Contains(p.TranslationRoomId) &&
                        TranslationRoomParticipantStatuses.SeatHolding.Contains(p.Status))
            .GroupBy(p => p.TranslationRoomId)
            .Select(g => new { RoomId = g.Key, Seats = g.Count() })
            .ToDictionaryAsync(x => x.RoomId, x => x.Seats, ct);
    }

    public async Task<Dictionary<Guid, int>> CountEverJoinedByRoomsAsync(
        IReadOnlyCollection<Guid> roomIds,
        CancellationToken ct = default)
    {
        if (roomIds.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        var ids = roomIds as List<Guid> ?? roomIds.ToList();

        // No status filter: the question is who turned up, not who is still here. Counted
        // DISTINCT by UserId because a participant who dropped and rejoined can hold more than
        // one row for the same room, and they attended once.
        return await _dbSet
            .Where(p => ids.Contains(p.TranslationRoomId))
            .GroupBy(p => p.TranslationRoomId)
            .Select(g => new { RoomId = g.Key, Attended = g.Select(p => p.UserId).Distinct().Count() })
            .ToDictionaryAsync(x => x.RoomId, x => x.Attended, ct);
    }
}
