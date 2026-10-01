using System;

namespace WarpTalk.BillingService.Domain.Entities;

/// <summary>
/// An incident a provider published on its own status page (statuspage.io-compatible
/// <c>/api/v2/incidents.json</c>), stored by ProviderStatusPollWorker so the 90-day uptime row keeps
/// incidents older than the page's own list (the latest 25–50). Shown beside our call outcomes,
/// never instead of them.
/// </summary>
public sealed class ProviderStatusIncident
{
    public Guid Id { get; set; }

    public string Provider { get; set; } = string.Empty;

    /// <summary>The status page's own incident id.</summary>
    public string ExternalId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>none | minor | major | critical | maintenance.</summary>
    public string Impact { get; set; } = string.Empty;

    /// <summary>investigating | identified | monitoring | resolved | postmortem | …</summary>
    public string Status { get; set; } = string.Empty;

    public DateTime StartedAt { get; set; }

    public DateTime? ResolvedAt { get; set; }

    public string? Url { get; set; }

    public DateTime SyncedAt { get; set; }
}
