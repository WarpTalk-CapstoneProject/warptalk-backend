using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

/// <summary>subscription.provider_status_incidents — incidents from each provider's public status page.</summary>
public interface IProviderStatusIncidentRepository : IGenericRepository<ProviderStatusIncident>
{
    /// <summary>Inserts new incidents and updates known ones (by provider + external id). One SaveChanges.</summary>
    Task<int> UpsertAsync(string provider, IReadOnlyCollection<ProviderStatusIncident> incidents, DateTime syncedAt, CancellationToken ct = default);

    /// <summary>Incidents of the provider that overlap [from, to) (unresolved ones overlap everything after they started).</summary>
    Task<IReadOnlyList<ProviderStatusIncident>> GetOverlappingAsync(string provider, DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>
    /// The start of the oldest stored incident. The status page lists its latest incidents
    /// contiguously, so everything since this instant is covered by the stored list.
    /// </summary>
    Task<DateTime?> GetOldestStartedAtAsync(string provider, CancellationToken ct = default);

    /// <summary>G12 inbox: incidents of any provider not resolved yet that started after <paramref name="since"/>.</summary>
    Task<IReadOnlyList<ProviderStatusIncident>> GetUnresolvedSinceAsync(DateTime since, CancellationToken ct = default);
}
