using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.BillingService.Application.Interfaces;

/// <summary>
/// A Stripe FX quote for USD→VND. <paramref name="BaseRate"/> is VND per US dollar before Stripe's FX
/// fee (<c>rate_details.base_rate</c>); <paramref name="ExchangeRate"/> is after it.
/// </summary>
public sealed record StripeFxQuote(string Id, DateTime CreatedAt, decimal BaseRate, decimal ExchangeRate, decimal? FxFeeRate);

/// <summary>One VND charge Stripe converted into the USD balance: VND per US dollar, as applied.</summary>
public sealed record StripeChargeConversion(string BalanceTransactionId, DateTime CreatedAt, decimal VndPerUsd);

/// <summary>Stripe's FX data. Both calls throw on a transport or API failure; the caller decides the fallback.</summary>
public interface IStripeFxClient
{
    /// <summary>False when no Stripe secret key is configured: nothing to ask.</summary>
    bool IsConfigured { get; }

    Task<StripeFxQuote> GetUsdToVndQuoteAsync(CancellationToken ct = default);

    Task<IReadOnlyList<StripeChargeConversion>> GetVndChargeConversionsAsync(DateTime since, CancellationToken ct = default);
}
