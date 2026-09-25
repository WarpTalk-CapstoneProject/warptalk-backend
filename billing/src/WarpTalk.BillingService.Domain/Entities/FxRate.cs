using System;

namespace WarpTalk.BillingService.Domain.Entities;

/// <summary>
/// subscription.fx_rates — one recorded exchange rate for one UTC day and one source
/// (migration 20260924150000). <see cref="Rate"/> is QUOTE per 1 BASE (USD→VND: VND per US dollar),
/// fee-exclusive; <see cref="FeeInclusiveRate"/> is what Stripe would actually apply after its FX fee.
/// </summary>
public class FxRate
{
    public Guid Id { get; set; }
    public string BaseCurrency { get; set; } = string.Empty;
    public string QuoteCurrency { get; set; } = string.Empty;
    public DateOnly RateDate { get; set; }
    public decimal Rate { get; set; }
    public string Source { get; set; } = string.Empty;
    public decimal? FeeInclusiveRate { get; set; }
    public string? SourceRef { get; set; }
    public DateTime FetchedAt { get; set; }
}
