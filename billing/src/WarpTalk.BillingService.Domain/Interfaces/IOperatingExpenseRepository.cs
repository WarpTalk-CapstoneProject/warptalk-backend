using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

/// <summary>subscription.operating_expenses (G12). Every read leaves soft-deleted rows out.</summary>
public interface IOperatingExpenseRepository : IGenericRepository<OperatingExpense>
{
    /// <summary>Live rows with <c>from &lt;= expense_date &lt;= to</c>, with their category, oldest first. Untracked.</summary>
    Task<IReadOnlyList<OperatingExpense>> ListInRangeAsync(DateOnly from, DateOnly to, CancellationToken ct = default);

    /// <summary>A live row by id, tracked.</summary>
    Task<OperatingExpense?> GetLiveAsync(Guid id, CancellationToken ct = default);

    /// <summary>Live series (recurrence monthly|yearly) with their category. Untracked.</summary>
    Task<IReadOnlyList<OperatingExpense>> ListSeriesAsync(CancellationToken ct = default);

    /// <summary>Live series whose next occurrence is due on or before <paramref name="dueBy"/>. Tracked.</summary>
    Task<IReadOnlyList<OperatingExpense>> ListSeriesDueAsync(DateOnly dueBy, CancellationToken ct = default);

    /// <summary>Whether the series already has an occurrence on <paramref name="date"/> (deleted or not).</summary>
    Task<bool> OccurrenceExistsAsync(Guid seriesId, DateOnly date, CancellationToken ct = default);

    /// <summary>Live planned rows dated on or before <paramref name="dueBy"/>, with their category. Untracked.</summary>
    Task<IReadOnlyList<OperatingExpense>> ListPlannedDueAsync(DateOnly dueBy, CancellationToken ct = default);
}
