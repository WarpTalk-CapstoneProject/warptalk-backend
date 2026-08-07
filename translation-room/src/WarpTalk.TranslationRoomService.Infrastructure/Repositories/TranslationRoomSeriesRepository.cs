using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;

namespace WarpTalk.TranslationRoomService.Infrastructure.Repositories;

public class TranslationRoomSeriesRepository : GenericRepository<TranslationRoomSeries>, ITranslationRoomSeriesRepository
{
    public TranslationRoomSeriesRepository(TranslationRoomDbContext dbContext) : base(dbContext)
    {
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TranslationRoomSeries>> GetSeriesNeedingMaterializationAsync(
        int limit, CancellationToken ct = default)
    {
        // Both halves of "still owes rooms" are expressed in SQL rather than filtered in memory:
        // a workspace that was abandoned six months ago has finished series, and the sweep must
        // not pay to load and re-examine every one of them on every pass.
        return await _dbSet
            .Where(s => s.Status == RecurrenceSeriesStatuses.Active
                        && (s.MaterializedThroughLocalDate == null
                            || s.MaterializedThroughLocalDate < s.EndsOnLocalDate))
            .OrderBy(s => s.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TranslationRoom>> GetCancellableOccurrencesAsync(
        Guid seriesId, DateTime fromUtc, CancellationToken ct = default)
    {
        // Restricted to the statuses CancelTranslationRoomAsync will actually accept
        // (SCHEDULED / WAITING), so cancelling a series never attempts to cancel a meeting that
        // is running, has ended, or was already cancelled — each of which would come back as a
        // refusal and log noise for something that is not an error.
        return await _context.TranslationRooms
            .Where(r => r.SeriesId == seriesId
                        && r.ScheduledAt != null
                        && r.ScheduledAt > fromUtc
                        && (r.Status == "SCHEDULED" || r.Status == "WAITING"))
            .OrderBy(r => r.ScheduledAt)
            .ToListAsync(ct);
    }
}
