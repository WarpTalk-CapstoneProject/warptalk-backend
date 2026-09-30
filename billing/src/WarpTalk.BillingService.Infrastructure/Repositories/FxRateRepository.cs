using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class FxRateRepository : GenericRepository<FxRate>, IFxRateRepository
{
    private readonly BillingDbContext _db;

    public FxRateRepository(BillingDbContext db) : base(db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<FxRate>> GetPairAsync(string baseCurrency, string quoteCurrency, CancellationToken ct = default)
        => await _db.FxRates.AsNoTracking()
            .Where(rate => rate.BaseCurrency == baseCurrency && rate.QuoteCurrency == quoteCurrency)
            .OrderBy(rate => rate.RateDate)
            .ThenBy(rate => rate.Source)
            .ToListAsync(ct);

    public async Task UpsertAsync(FxRate rate, CancellationToken ct = default)
    {
        // Read-then-write, as UpsertPricingConfigValueAsync does: billing stays on EF (no ON CONFLICT),
        // and one refresh at a time writes (the worker holds a lease). A concurrent insert of the same
        // key fails on the unique index rather than duplicating the day.
        var existing = await _db.FxRates.FirstOrDefaultAsync(row =>
            row.BaseCurrency == rate.BaseCurrency
            && row.QuoteCurrency == rate.QuoteCurrency
            && row.RateDate == rate.RateDate
            && row.Source == rate.Source, ct);

        var fetchedAt = DateTime.SpecifyKind(rate.FetchedAt, DateTimeKind.Utc);
        if (existing is null)
        {
            _db.FxRates.Add(new FxRate
            {
                Id = rate.Id == Guid.Empty ? Guid.NewGuid() : rate.Id,
                BaseCurrency = rate.BaseCurrency,
                QuoteCurrency = rate.QuoteCurrency,
                RateDate = rate.RateDate,
                Rate = rate.Rate,
                Source = rate.Source,
                FeeInclusiveRate = rate.FeeInclusiveRate,
                SourceRef = rate.SourceRef,
                FetchedAt = fetchedAt,
            });
        }
        else
        {
            existing.Rate = rate.Rate;
            existing.FeeInclusiveRate = rate.FeeInclusiveRate;
            existing.SourceRef = rate.SourceRef;
            existing.FetchedAt = fetchedAt;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string baseCurrency, string quoteCurrency, DateOnly rateDate, string source, CancellationToken ct = default)
    {
        var existing = await _db.FxRates.FirstOrDefaultAsync(row =>
            row.BaseCurrency == baseCurrency
            && row.QuoteCurrency == quoteCurrency
            && row.RateDate == rateDate
            && row.Source == source, ct);
        if (existing is null) return;

        _db.FxRates.Remove(existing);
        await _db.SaveChangesAsync(ct);
    }
}
