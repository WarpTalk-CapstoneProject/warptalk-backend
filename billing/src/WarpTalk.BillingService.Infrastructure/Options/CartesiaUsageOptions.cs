namespace WarpTalk.BillingService.Infrastructure.Options;

/// <summary>
/// Cartesia usage sync (section <c>Cartesia</c>; in production the env vars
/// <c>Cartesia__AdminApiKey</c> ← GitHub secret CARTESIA_ADMIN_API_KEY, and the optional
/// <c>Cartesia__UsageApiKeyId</c>).
///
/// With no <see cref="AdminApiKey"/> the sync is disabled — logged once — and Insights keep
/// estimating dubbing cost from the rate cards. The key is only ever placed in the Authorization
/// header of a request; nothing logs it.
/// </summary>
public sealed class CartesiaUsageOptions
{
    public const string SectionName = "Cartesia";

    public const string HttpClientName = "cartesia-usage";

    /// <summary>A Cartesia ADMIN API key (<c>sk_car_admin_…</c>). Standard keys are refused by /usage.</summary>
    public string? AdminApiKey { get; set; }

    /// <summary>
    /// Optional id (UUID) of the standard API key production TTS uses — GET /api-keys lists them. When
    /// set, only that key's credits are synced, so staging, local development and playground usage on
    /// the same Cartesia account do not count as production cost.
    /// </summary>
    public string? UsageApiKeyId { get; set; }

    public string BaseUrl { get; set; } = "https://api.cartesia.ai";

    /// <summary>The Cartesia-Version header; 2026-08-14 is the only version /usage/credits accepts.</summary>
    public string ApiVersion { get; set; } = "2026-08-14";

    /// <summary>How often today and the previous days are re-read. 0 disables the sync.</summary>
    public int UsageSyncIntervalMinutes { get; set; } = 10;

    /// <summary>UTC days re-read by every sync, today included: late-arriving usage lands on recent days.</summary>
    public int UsageRecentDays { get; set; } = 3;

    /// <summary>UTC days re-read by the daily backfill, today included.</summary>
    public int UsageBackfillDays { get; set; } = 35;

    public int UsageBackfillIntervalHours { get; set; } = 24;

    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>Longest wait between attempts while Cartesia keeps failing or rate-limiting.</summary>
    public int MaxBackoffMinutes { get; set; } = 60;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(AdminApiKey);

    public bool IsFilteredToApiKey => Guid.TryParse(UsageApiKeyId, out _);
}
