using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

/// <summary>subscription.provider_call_stats — our calls to each provider, per UTC hour, operation and model.</summary>
public interface IProviderCallStatRepository : IGenericRepository<ProviderCallStat>
{
    /// <summary>
    /// Merges one sync: each row is inserted, or its counters raised to the larger of stored and
    /// reported (never lowered). One SaveChanges. Returns how many rows changed.
    /// </summary>
    Task<int> MergeAsync(IReadOnlyCollection<ProviderCallStat> rows, DateTime syncedAt, CancellationToken ct = default);

    /// <summary>Rows with <c>hour_start</c> in [from, to); every provider when <paramref name="provider"/> is null.</summary>
    Task<IReadOnlyList<ProviderCallStat>> GetRangeAsync(string? provider, DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>The first hour recorded for the provider, or null when none ever was.</summary>
    Task<DateTime?> GetFirstHourAsync(string provider, CancellationToken ct = default);
}
