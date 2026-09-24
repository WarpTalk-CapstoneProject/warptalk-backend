namespace WarpTalk.BillingService.Infrastructure.Options;

/// <summary>
/// ProviderStatus section. <see cref="Pages"/>: provider key → public status page base URL
/// (statuspage.io v2 API). Empty = no status pages polled; the page then relies on our call data only.
/// </summary>
public sealed class ProviderStatusOptions
{
    public const string SectionName = "ProviderStatus";

    public Dictionary<string, string> Pages { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Status page poll interval; 0 switches polling off.</summary>
    public int PollIntervalMinutes { get; set; } = 5;

    /// <summary>Provider-call copy interval (Redis → Postgres); 0 switches it off.</summary>
    public int CallStatsSyncIntervalMinutes { get; set; } = 2;
}
