using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Domain.Interfaces;

public interface ITranslationRoomSeriesRepository : IGenericRepository<TranslationRoomSeries>
{
    /// <summary>
    /// WT-327: every series the materialisation sweep still owes rooms for — ACTIVE only, and
    /// only those whose horizon has not already reached their end date. A series that is
    /// cancelled or fully materialised is excluded in SQL rather than skipped in memory, so an
    /// abandoned workspace's finished series costs the sweep nothing at all.
    /// </summary>
    Task<IReadOnlyList<TranslationRoomSeries>> GetSeriesNeedingMaterializationAsync(int limit, CancellationToken ct = default);

    /// <summary>
    /// WT-327: the future occurrences of a series that a series-level cancel must take with it.
    /// "Future" is measured against <paramref name="fromUtc"/> and restricted to the statuses
    /// <c>CancelTranslationRoomAsync</c> will actually accept, so a series cancel never tries to
    /// cancel a meeting that already happened or is happening right now.
    /// </summary>
    Task<IReadOnlyList<TranslationRoom>> GetCancellableOccurrencesAsync(Guid seriesId, DateTime fromUtc, CancellationToken ct = default);
}
