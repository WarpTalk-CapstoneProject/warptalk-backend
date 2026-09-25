using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class ProviderStatusIncidentRepository : GenericRepository<ProviderStatusIncident>, IProviderStatusIncidentRepository
{
    private readonly BillingDbContext _db;

    public ProviderStatusIncidentRepository(BillingDbContext db) : base(db)
    {
        _db = db;
    }

    public async Task<int> UpsertAsync(string provider, IReadOnlyCollection<ProviderStatusIncident> incidents, DateTime syncedAt, CancellationToken ct = default)
    {
        if (incidents.Count == 0) return 0;
        var stamp = DateTime.SpecifyKind(syncedAt, DateTimeKind.Utc);
        var ids = incidents.Select(i => i.ExternalId).Distinct().ToArray();
        var existing = await _db.ProviderStatusIncidents
            .Where(i => i.Provider == provider && ids.Contains(i.ExternalId))
            .ToDictionaryAsync(i => i.ExternalId, ct);

        foreach (var incident in incidents.GroupBy(i => i.ExternalId).Select(g => g.Last()))
        {
            if (!existing.TryGetValue(incident.ExternalId, out var row))
            {
                row = new ProviderStatusIncident { Id = Guid.NewGuid(), Provider = provider, ExternalId = incident.ExternalId };
                _db.ProviderStatusIncidents.Add(row);
                existing[incident.ExternalId] = row;
            }

            row.Name = incident.Name;
            row.Impact = incident.Impact;
            row.Status = incident.Status;
            row.StartedAt = DateTime.SpecifyKind(incident.StartedAt, DateTimeKind.Utc);
            row.ResolvedAt = incident.ResolvedAt is { } resolved ? DateTime.SpecifyKind(resolved, DateTimeKind.Utc) : null;
            row.Url = incident.Url;
            row.SyncedAt = stamp;
        }

        return await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ProviderStatusIncident>> GetOverlappingAsync(string provider, DateTime from, DateTime to, CancellationToken ct = default)
        => await _db.ProviderStatusIncidents
            .AsNoTracking()
            .Where(i => i.Provider == provider && i.StartedAt < to && (i.ResolvedAt == null || i.ResolvedAt > from))
            .OrderByDescending(i => i.StartedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ProviderStatusIncident>> GetUnresolvedSinceAsync(DateTime since, CancellationToken ct = default)
        => await _db.ProviderStatusIncidents
            .AsNoTracking()
            .Where(i => i.ResolvedAt == null && i.StartedAt >= since)
            .OrderByDescending(i => i.StartedAt)
            .ToListAsync(ct);

    public async Task<DateTime?> GetOldestStartedAtAsync(string provider, CancellationToken ct = default)
        => await _db.ProviderStatusIncidents
            .AsNoTracking()
            .Where(i => i.Provider == provider)
            .MinAsync(i => (DateTime?)i.StartedAt, ct);
}
