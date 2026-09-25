using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Domain.Services;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class ProviderCallStatRepository : GenericRepository<ProviderCallStat>, IProviderCallStatRepository
{
    private readonly BillingDbContext _db;

    public ProviderCallStatRepository(BillingDbContext db) : base(db)
    {
        _db = db;
    }

    public async Task<int> MergeAsync(IReadOnlyCollection<ProviderCallStat> rows, DateTime syncedAt, CancellationToken ct = default)
    {
        if (rows.Count == 0) return 0;
        var stamp = DateTime.SpecifyKind(syncedAt, DateTimeKind.Utc);

        // Plain EF, not ON CONFLICT: billing stays on EF outside its two approved raw-SQL primitives
        // (see ProviderUsageDailyRepository). A sync is two UTC days of one hash: a few hundred rows.
        var hours = rows.Select(r => r.HourStart).Distinct().ToArray();
        var providers = rows.Select(r => r.Provider).Distinct().ToArray();
        var existing = await _db.ProviderCallStats
            .Where(r => providers.Contains(r.Provider) && hours.Contains(r.HourStart))
            .ToListAsync(ct);
        var byKey = existing.ToDictionary(r => (r.Provider, r.HourStart, r.Operation, r.Model));

        var changed = 0;
        foreach (var row in rows)
        {
            var key = (row.Provider, row.HourStart, row.Operation, row.Model);
            if (byKey.TryGetValue(key, out var current))
            {
                if (ProviderCallStatMerge.RaiseTo(current, row))
                {
                    current.SyncedAt = stamp;
                    changed++;
                }

                continue;
            }

            var added = ProviderCallStatMerge.Copy(row);
            added.Id = Guid.NewGuid();
            added.HourStart = DateTime.SpecifyKind(row.HourStart, DateTimeKind.Utc);
            added.SyncedAt = stamp;
            _db.ProviderCallStats.Add(added);
            byKey[key] = added;
            changed++;
        }

        if (changed > 0) await _db.SaveChangesAsync(ct);
        return changed;
    }

    public async Task<IReadOnlyList<ProviderCallStat>> GetRangeAsync(string? provider, DateTime from, DateTime to, CancellationToken ct = default)
        => await _db.ProviderCallStats
            .AsNoTracking()
            .Where(r => (provider == null || r.Provider == provider) && r.HourStart >= from && r.HourStart < to)
            .OrderBy(r => r.HourStart)
            .ToListAsync(ct);

    public async Task<DateTime?> GetFirstHourAsync(string provider, CancellationToken ct = default)
        => await _db.ProviderCallStats
            .AsNoTracking()
            .Where(r => r.Provider == provider)
            .MinAsync(r => (DateTime?)r.HourStart, ct);
}
