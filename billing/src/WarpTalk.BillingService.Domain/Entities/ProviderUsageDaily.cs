using System;

namespace WarpTalk.BillingService.Domain.Entities;

/// <summary>
/// One day of usage MEASURED by an AI provider (subscription.provider_usage_daily), as the provider's
/// own usage API reports it — today only Cartesia's <c>GET /usage/credits</c>.
///
/// Distinct from usage_records / credit_transactions, which are what WarpTalk metered and charged
/// its customers. This is what the provider says it charged WarpTalk; admin Insights prices the
/// dubbing charge types from it instead of from an assumed characters-per-second rate.
///
/// One row per (provider, UTC day, group kind, group id). <see cref="GroupKind"/> is:
///   <c>total</c>      — group id <c>all</c>: every credit that day. Its presence is also the marker that
///                       the day was synced at all, even when nothing was used (credits 0).
///   <c>capability</c> — Cartesia's capability ids (TTS, STT, …).
///   <c>model</c>      — Cartesia's model ids (sonic-3.5, …).
/// </summary>
public sealed class ProviderUsageDaily
{
    public Guid Id { get; set; }

    public string Provider { get; set; } = string.Empty;

    /// <summary>The provider's calendar day. Cartesia buckets by UTC day.</summary>
    public DateOnly UsageDate { get; set; }

    public string GroupKind { get; set; } = string.Empty;

    public string GroupId { get; set; } = string.Empty;

    public string? GroupLabel { get; set; }

    public long Credits { get; set; }

    /// <summary>When this row was last written from the provider's API (UTC).</summary>
    public DateTime SyncedAt { get; set; }
}
