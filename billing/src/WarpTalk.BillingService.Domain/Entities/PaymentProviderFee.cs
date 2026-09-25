using System;

namespace WarpTalk.BillingService.Domain.Entities;

/// <summary>
/// The processing fee a payment provider kept on one paid payment (subscription.payment_provider_fees),
/// read from Stripe's balance transaction by StripeFeeSyncWorker. Amounts are in major units of
/// <see cref="Currency"/>, the account's settlement currency — not necessarily the payment's.
/// </summary>
public sealed class PaymentProviderFee
{
    public const string StatusOk = "ok";
    public const string StatusNotFound = "not_found";
    public const string StatusError = "error";

    public Guid Id { get; set; }

    public Guid PaymentId { get; set; }

    public string Provider { get; set; } = string.Empty;

    /// <summary>ok | not_found (Stripe has no charge for it; not asked again) | error (retried later).</summary>
    public string Status { get; set; } = StatusOk;

    public string? BalanceTransactionId { get; set; }

    public string? ChargeId { get; set; }

    /// <summary>Upper-case ISO code of the settlement currency.</summary>
    public string? Currency { get; set; }

    public decimal? Amount { get; set; }

    public decimal? Fee { get; set; }

    public decimal? Net { get; set; }

    /// <summary>Charge currency → settlement currency, when Stripe converted.</summary>
    public decimal? ExchangeRate { get; set; }

    /// <summary>When the balance transaction was created (UTC).</summary>
    public DateTime? OccurredAt { get; set; }

    /// <summary>Why the fee could not be read. Never contains a key.</summary>
    public string? Error { get; set; }

    public DateTime FetchedAt { get; set; }
}
