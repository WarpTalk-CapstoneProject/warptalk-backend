using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

/// <summary>subscription.fx_rates — the recorded exchange rates, one row per (pair, UTC day, source).</summary>
public interface IFxRateRepository : IGenericRepository<FxRate>
{
    /// <summary>Every recorded row of the pair, oldest day first. The table holds a few rows per day.</summary>
    Task<IReadOnlyList<FxRate>> GetPairAsync(string baseCurrency, string quoteCurrency, CancellationToken ct = default);

    /// <summary>Inserts or replaces the row of (pair, day, source). Saves.</summary>
    Task UpsertAsync(FxRate rate, CancellationToken ct = default);

    /// <summary>Removes the row of (pair, day, source) if there is one. Saves.</summary>
    Task DeleteAsync(string baseCurrency, string quoteCurrency, DateOnly rateDate, string source, CancellationToken ct = default);
}
