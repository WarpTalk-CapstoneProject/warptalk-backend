using System;

namespace WarpTalk.BillingService.Domain.Entities;

/// <summary>
/// Immutable priced row in subscription.usage_rate_card. Settlement resolves the
/// active row for a billing identity (charge type + unit + currency + provider +
/// model + language pair) and snapshots its unit price onto the credit
/// transaction, so rows are superseded rather than edited in place.
/// </summary>
public partial class UsageRateCard
{
    public Guid Id { get; set; }

    public string ChargeType { get; set; } = null!;

    /// <summary>
    /// Nullable in the database (added by migration 005 without a backfill), so
    /// pre-Phase-2 rows can still be read.
    /// </summary>
    public string? Unit { get; set; }

    public string? Provider { get; set; }

    public string? Model { get; set; }

    public string? SourceLanguageCode { get; set; }

    public string? TargetLanguageCode { get; set; }

    public decimal UnitPrice { get; set; }

    public string Currency { get; set; } = null!;

    public decimal? ProviderUnitCost { get; set; }

    public decimal? MarkupMultiplier { get; set; }

    public DateTime EffectiveFrom { get; set; }

    public DateTime? EffectiveTo { get; set; }

    public bool IsActive { get; set; }

    public string? Notes { get; set; }
}
