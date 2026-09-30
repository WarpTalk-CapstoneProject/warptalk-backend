using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class ProviderUsageDailyRepository : GenericRepository<ProviderUsageDaily>, IProviderUsageDailyRepository
{
    private readonly BillingDbContext _db;

    public ProviderUsageDailyRepository(BillingDbContext db) : base(db)
    {
        _db = db;
    }

    public async Task ReplaceWindowAsync(
        string provider,
        DateOnly fromDate,
        DateOnly toDate,
        IReadOnlyCollection<string> groupKinds,
        IReadOnlyCollection<ProviderUsageDaily> rows,
        DateTime syncedAt,
        CancellationToken ct = default)
    {
        var kinds = groupKinds.Distinct(StringComparer.Ordinal).ToArray();
        var stamp = DateTime.SpecifyKind(syncedAt, DateTimeKind.Utc);

        // Plain EF Core, not INSERT ... ON CONFLICT: billing's data access stays on EF outside the two
        // approved raw-SQL primitives (warptalk-infrastructure check-production-deployment.sh). One
        // SaveChanges is one transaction, and a sync is a few hundred rows at most.
        var existing = await _db.ProviderUsageDaily
            .Where(row => row.Provider == provider
                          && row.UsageDate >= fromDate
                          && row.UsageDate <= toDate
                          && kinds.Contains(row.GroupKind))
            .ToListAsync(ct);
        var stale = existing.ToDictionary(row => (row.UsageDate, row.GroupKind, row.GroupId));
        var written = new Dictionary<(DateOnly, string, string), ProviderUsageDaily>();

        foreach (var row in rows)
        {
            var key = (row.UsageDate, row.GroupKind, row.GroupId);
            if (written.TryGetValue(key, out var same))
            {
                // The same group reported twice for a day: one row, both counts.
                same.Credits += row.Credits;
                continue;
            }

            if (stale.Remove(key, out var current))
            {
                current.GroupLabel = row.GroupLabel;
                current.Credits = row.Credits;
                current.SyncedAt = stamp;
                written[key] = current;
                continue;
            }

            var added = new ProviderUsageDaily
            {
                Id = row.Id == Guid.Empty ? Guid.NewGuid() : row.Id,
                Provider = provider,
                UsageDate = row.UsageDate,
                GroupKind = row.GroupKind,
                GroupId = row.GroupId,
                GroupLabel = row.GroupLabel,
                Credits = row.Credits,
                SyncedAt = stamp,
            };
            _db.ProviderUsageDaily.Add(added);
            written[key] = added;
        }

        // Whatever this sync did not report for the same days and kinds is no longer the provider's
        // answer: without this, a group that disappears from a day would keep its old credits forever.
        _db.ProviderUsageDaily.RemoveRange(stale.Values);

        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ProviderUsageDaily>> GetDaysAsync(
        string provider,
        string groupKind,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken ct = default)
        => await _db.ProviderUsageDaily
            .AsNoTracking()
            .Where(row => row.Provider == provider
                          && row.GroupKind == groupKind
                          && row.UsageDate >= fromDate
                          && row.UsageDate <= toDate)
            .OrderBy(row => row.UsageDate)
            .ThenBy(row => row.GroupId)
            .ToListAsync(ct);

    public async Task<DateTime?> GetLastSyncedAtAsync(string provider, CancellationToken ct = default)
        => await _db.ProviderUsageDaily
            .AsNoTracking()
            .Where(row => row.Provider == provider)
            .MaxAsync(row => (DateTime?)row.SyncedAt, ct);
}
