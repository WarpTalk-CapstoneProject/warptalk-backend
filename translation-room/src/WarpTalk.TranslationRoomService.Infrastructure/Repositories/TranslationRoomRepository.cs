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

    /// <summary>
    /// The statuses that mean "this room is carrying audio right now".
    ///
    /// PAUSED is included: translation has stopped but the call has not, everyone is still
    /// connected, and a count that dropped it would report an in-progress meeting as finished.
    /// WAITING is not — nobody is in the room yet, the host has not opened it.
    /// </summary>
    private static readonly string[] LiveStatuses =
        [nameof(RoomStatus.IN_PROGRESS), nameof(RoomStatus.PAUSED)];

    public async Task<(IReadOnlyList<AdminMeetingRow> Items, int Total)> GetAdminDirectoryAsync(
        AdminMeetingFilter filter,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var query = ApplyAdminFilters(_dbSet.AsNoTracking(), filter);

        var total = await query.CountAsync(ct);

        // Sorted on the ENTITY, before the projection — which is what makes projecting straight
        // into a positional record safe. Ordering a record projection by one of its own properties
        // does not translate, and that defect has already cost this codebase a 500 on every call
        // to billing's usage-by-member.
        var rows = await ApplyAdminSort(query, filter.Sort)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new AdminMeetingRow(
                r.Id,
                r.WorkspaceId,
                r.Title,
                r.TranslationRoomCode,
                r.Status,
                r.TranslationRoomType,
                r.SourceLanguage,
                r.TargetLanguages,
                r.ScheduledAt,
                r.StartedAt,
                r.EndedAt,
                r.DurationSeconds,
                r.CreatedAt))
            .ToListAsync(ct);

        return (rows, total);
    }

    public async Task<(int Live, int StartedSince)> GetAdminCountsAsync(
        DateTime since,
        CancellationToken ct = default)
    {
        var live = await _dbSet
            .AsNoTracking()
            .CountAsync(r => r.DeletedAt == null && LiveStatuses.Contains(r.Status), ct);

        // StartedAt, not CreatedAt: a room booked last week and run today belongs to today, and
        // counting by creation would attribute it to the day somebody filled in a form.
        var startedSince = await _dbSet
            .AsNoTracking()
            .CountAsync(r => r.DeletedAt == null && r.StartedAt != null && r.StartedAt >= since, ct);

        return (live, startedSince);
    }

    private static IQueryable<TranslationRoom> ApplyAdminFilters(
        IQueryable<TranslationRoom> query,
        AdminMeetingFilter filter)
    {
        query = query.Where(r => r.DeletedAt == null);

        if (filter.WorkspaceId is { } workspaceId)
        {
            query = query.Where(r => r.WorkspaceId == workspaceId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            query = filter.Status == "live"
                ? query.Where(r => LiveStatuses.Contains(r.Status))
                : query.Where(r => r.Status == filter.Status);
        }

        // The window is measured against when the meeting HAPPENED — started if it ever did,
        // otherwise the time it was booked for, otherwise when the row was made. Filtering on
        // CreatedAt alone would put an instant meeting and a meeting booked a month ago on the
        // same day, which is the confusion the workspace list already had to fix.
        if (filter.From is { } from)
        {
            query = query.Where(r => (r.StartedAt ?? r.ScheduledAt ?? r.CreatedAt) >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(r => (r.StartedAt ?? r.ScheduledAt ?? r.CreatedAt) <= to);
        }

        return query;
    }

    private static IQueryable<TranslationRoom> ApplyAdminSort(
        IQueryable<TranslationRoom> query,
        string sort) => sort switch
        {
            "recent_asc" => query.OrderBy(r => r.StartedAt ?? r.ScheduledAt ?? r.CreatedAt),
            "duration_desc" => query.OrderByDescending(r => r.DurationSeconds),
            _ => query.OrderByDescending(r => r.StartedAt ?? r.ScheduledAt ?? r.CreatedAt),
        };

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
