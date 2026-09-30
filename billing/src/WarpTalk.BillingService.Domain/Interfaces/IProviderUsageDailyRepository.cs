using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

/// <summary>subscription.provider_usage_daily — provider-measured usage per UTC day.</summary>
public interface IProviderUsageDailyRepository : IGenericRepository<ProviderUsageDaily>
{
    /// <summary>
    /// Writes one sync of <paramref name="provider"/> for the days <paramref name="fromDate"/> ..
    /// <paramref name="toDate"/> (inclusive): every row is inserted or updated on
    /// (provider, usage_date, group_kind, group_id), stamped <paramref name="syncedAt"/>, and a row of
    /// the same kinds and days that this sync no longer reports is removed — the provider's answer for
    /// a day replaces the previous one rather than accumulating beside it. One SaveChanges, so one transaction.
    /// </summary>
    Task ReplaceWindowAsync(
        string provider,
        DateOnly fromDate,
        DateOnly toDate,
        IReadOnlyCollection<string> groupKinds,
        IReadOnlyCollection<ProviderUsageDaily> rows,
        DateTime syncedAt,
        CancellationToken ct = default);

    /// <summary>Rows of one provider and group kind for the UTC days <paramref name="fromDate"/> .. <paramref name="toDate"/> inclusive.</summary>
    Task<IReadOnlyList<ProviderUsageDaily>> GetDaysAsync(
        string provider,
        string groupKind,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken ct = default);

    /// <summary>The newest synced_at of the provider, or null when it was never synced.</summary>
    Task<DateTime?> GetLastSyncedAtAsync(string provider, CancellationToken ct = default);
}
