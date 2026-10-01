using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class PaymentProviderFeeRepository : GenericRepository<PaymentProviderFee>, IPaymentProviderFeeRepository
{
    private readonly BillingDbContext _db;

    public PaymentProviderFeeRepository(BillingDbContext db) : base(db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<PaymentAwaitingFee>> GetAwaitingAsync(
        string provider, DateTime since, DateTime retryErrorsBefore, int take, CancellationToken ct = default)
    {
        var key = provider.Trim().ToLowerInvariant();
        var rows = await (
                from p in _db.Payments.IgnoreQueryFilters().AsNoTracking()
                join f in _db.PaymentProviderFees.AsNoTracking() on p.Id equals f.PaymentId into fees
                from f in fees.DefaultIfEmpty()
                where p.Provider.ToLower() == key
                      && p.Status == PaymentConstants.PaymentStatuses.Paid
                      && p.ProviderTransactionId != null
                      && (p.PaidAt ?? p.UpdatedAt) >= since
                      && (f == null || (f.Status == PaymentProviderFee.StatusError && f.FetchedAt < retryErrorsBefore))
                orderby p.PaidAt ?? p.UpdatedAt
                select new { p.Id, p.ProviderTransactionId, PaidAt = p.PaidAt ?? p.UpdatedAt })
            .Take(take)
            .ToListAsync(ct);

        return rows
            .Select(r => new PaymentAwaitingFee(r.Id, r.ProviderTransactionId!, DateTime.SpecifyKind(r.PaidAt, DateTimeKind.Utc)))
            .ToList();
    }

    public async Task UpsertAsync(PaymentProviderFee fee, CancellationToken ct = default)
    {
        var existing = await _db.PaymentProviderFees.FirstOrDefaultAsync(f => f.PaymentId == fee.PaymentId, ct);
        if (existing is null)
        {
            if (fee.Id == Guid.Empty) fee.Id = Guid.NewGuid();
            _db.PaymentProviderFees.Add(fee);
        }
        else
        {
            existing.Provider = fee.Provider;
            existing.Status = fee.Status;
            existing.BalanceTransactionId = fee.BalanceTransactionId;
            existing.ChargeId = fee.ChargeId;
            existing.Currency = fee.Currency;
            existing.Amount = fee.Amount;
            existing.Fee = fee.Fee;
            existing.Net = fee.Net;
            existing.ExchangeRate = fee.ExchangeRate;
            existing.OccurredAt = fee.OccurredAt;
            existing.Error = fee.Error;
            existing.FetchedAt = fee.FetchedAt;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<PaymentFeeRow>> GetPaidInAsync(string provider, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var key = provider.Trim().ToLowerInvariant();
        var rows = await (
                from f in _db.PaymentProviderFees.AsNoTracking()
                join p in _db.Payments.IgnoreQueryFilters().AsNoTracking() on f.PaymentId equals p.Id
                where f.Provider == key
                      && (p.PaidAt ?? p.UpdatedAt) >= @from
                      && (p.PaidAt ?? p.UpdatedAt) < to
                select new { f.PaymentId, PaidAt = p.PaidAt ?? p.UpdatedAt, f.Status, f.Currency, f.Fee, f.BalanceTransactionId })
            .ToListAsync(ct);

        // A subscription checkout is recorded twice (its cs_ session and its in_ invoice) and both
        // resolve to the same charge: one balance transaction is one fee.
        return rows
            .GroupBy(r => r.BalanceTransactionId ?? r.PaymentId.ToString())
            .Select(g => g.OrderBy(r => r.PaidAt).First())
            .Select(r => new PaymentFeeRow(r.PaymentId, DateTime.SpecifyKind(r.PaidAt, DateTimeKind.Utc), r.Status, r.Currency, r.Fee))
            .ToList();
    }
}
