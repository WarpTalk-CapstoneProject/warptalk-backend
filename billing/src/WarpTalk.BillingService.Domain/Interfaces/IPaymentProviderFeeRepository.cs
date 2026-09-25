using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

/// <summary>A paid payment whose provider fee has not been read yet (or whose last read failed long enough ago).</summary>
public sealed record PaymentAwaitingFee(Guid PaymentId, string ProviderTransactionId, DateTime PaidAt);

/// <summary>A read fee with the instant its payment was paid, for the Providers page's hourly cost.</summary>
public sealed record PaymentFeeRow(Guid PaymentId, DateTime PaidAt, string Status, string? Currency, decimal? Fee);

/// <summary>subscription.payment_provider_fees — one row per paid payment.</summary>
public interface IPaymentProviderFeeRepository : IGenericRepository<PaymentProviderFee>
{
    /// <summary>
    /// Paid payments of <paramref name="provider"/> paid since <paramref name="since"/> with no fee
    /// row, or an <c>error</c> row fetched before <paramref name="retryErrorsBefore"/>; oldest first,
    /// at most <paramref name="take"/>.
    /// </summary>
    Task<IReadOnlyList<PaymentAwaitingFee>> GetAwaitingAsync(
        string provider, DateTime since, DateTime retryErrorsBefore, int take, CancellationToken ct = default);

    /// <summary>Inserts or replaces the row of <see cref="PaymentProviderFee.PaymentId"/>. One SaveChanges.</summary>
    Task UpsertAsync(PaymentProviderFee fee, CancellationToken ct = default);

    /// <summary>Fee rows of the provider's payments paid in [from, to), with the paid instant.</summary>
    Task<IReadOnlyList<PaymentFeeRow>> GetPaidInAsync(string provider, DateTime from, DateTime to, CancellationToken ct = default);
}
