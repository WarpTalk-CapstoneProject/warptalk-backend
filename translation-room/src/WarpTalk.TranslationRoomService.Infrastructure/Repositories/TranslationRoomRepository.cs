using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranslationRoomService.Domain.Constants;
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
            foreach (var status in excludedStatuses)
            {
                if (Enum.TryParse<WarpTalk.TranslationRoomService.Domain.Enums.RoomStatus>(status, true, out var roomStatus))
                {
                    query = query.Where(r => r.Status != roomStatus.ToString());
                }
            }
        }

        return await query.AnyAsync(cancellationToken);
    }

    /// <summary>
    /// The room a code opens.
    ///
    /// A one-off room is the only room with its code and this is a lookup. A RECURRING booking
    /// shares one code across every occurrence — one meeting, one link, the way Zoom does it — so
    /// the same code has to resolve to a different room tomorrow than it does today. The order is:
    ///
    ///   1. the occurrence that is live right now (IN_PROGRESS, PAUSED, WAITING). Somebody
    ///      clicking the link during the meeting means that meeting, even if the next one is
    ///      nearer on the clock because today's overran.
    ///   2. otherwise the next one due — the soonest scheduled_at at or after now.
    ///   3. otherwise the most recent one, so a link followed after the series has finished lands
    ///      on the last meeting rather than on nothing.
    ///
    /// Deliberately resolved here and not at the call sites: join, preflight and the invite link
    /// all go through this method, and a rule this one implemented three times is a rule that ends
    /// up meaning three things.
    /// </summary>
    public async Task<TranslationRoom?> GetByCodeAsync(string roomCode, IEnumerable<string>? excludedStatuses = null, CancellationToken cancellationToken = default)
    {
        var query = _dbSet.Where(r => r.TranslationRoomCode == roomCode && r.DeletedAt == null);

        if (excludedStatuses != null && excludedStatuses.Any())
        {
            foreach (var status in excludedStatuses)
            {
                if (Enum.TryParse<WarpTalk.TranslationRoomService.Domain.Enums.RoomStatus>(status, true, out var roomStatus))
                {
                    query = query.Where(r => r.Status != roomStatus.ToString());
                }
            }
        }

        var candidates = await query.ToListAsync(cancellationToken);
        if (candidates.Count <= 1) return candidates.FirstOrDefault();

        var now = DateTime.UtcNow;

        var live = candidates
            .Where(r => r.Status == "IN_PROGRESS" || r.Status == "PAUSED" || r.Status == "WAITING")
            .OrderBy(r => r.ScheduledAt ?? r.StartedAt ?? r.CreatedAt)
            .FirstOrDefault();
        if (live is not null) return live;

        var next = candidates
            .Where(r => (r.ScheduledAt ?? r.CreatedAt) >= now)
            .OrderBy(r => r.ScheduledAt ?? r.CreatedAt)
            .FirstOrDefault();
        if (next is not null) return next;

        return candidates
            .OrderByDescending(r => r.ScheduledAt ?? r.CreatedAt)
            .First();
    }

    public async Task<List<TranslationRoom>> GetHistoryByUserIdAsync(Guid userId, int limit, int offset, CancellationToken ct = default)
    {
        var terminalStatuses = TranslationRoomConstants.TerminalStatuses;

        var query = _dbSet
            .Include(r => r.TranslationRoomParticipants)
            .Include(r => r.TranslationRoomArtifacts)
            .Where(r => (r.Status == "ENDED" || r.Status == "CANCELLED" || r.Status == "EXPIRED") && r.DeletedAt == null &&
                        (r.HostId == userId
                            // WT-359: a transferred-to host must still find the room in their list.
                            || r.ActiveHostId == userId
                            || r.TranslationRoomParticipants.Any(p => p.UserId == userId)))
            .OrderByDescending(r => r.CreatedAt)
            .Skip(offset)
            .Take(limit);

        return await query.ToListAsync(ct);
    }

    public Task<int> CountActiveByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default)
        => _dbSet.CountAsync(
            room => room.WorkspaceId == workspaceId
                && room.DeletedAt == null
                && (room.Status == "WAITING"
                    || room.Status == "IN_PROGRESS"
                    || room.Status == "PAUSED"),
            ct);
}
