using System;
using System.Collections.Generic;

namespace WarpTalk.BillingService.Application.Services;

/// <summary>
/// Facts about provider configuration the Providers page may state — whether something is set, never
/// its value. Built once in Program from configuration.
/// </summary>
public sealed class AdminProvidersOptions
{
    /// <summary>Provider key → public status page base URL (ProviderStatus:Pages:{key}). Public URLs, not secrets.</summary>
    public IReadOnlyDictionary<string, string> StatusPages { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool StripeSecretKeyConfigured { get; init; }

    public bool StripeWebhookSecretConfigured { get; init; }
}
