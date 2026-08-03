using System;
using System.Collections.Generic;
using System.Linq;

namespace WarpTalk.Shared.Authorization;

/// <summary>
/// Strips secrets out of audit before/after summaries (WT-210).
/// </summary>
/// <remarks>
/// Applied by the publisher AND again by the consumer before persistence. Redacting twice is
/// cheap; a provider key written into an append-only table that has no DELETE grant is not
/// removable. Matching is on the key name because the values are opaque by definition.
/// </remarks>
public static class AdminAuditRedaction
{
    public const string RedactedPlaceholder = "[redacted]";

    private static readonly string[] SensitiveKeyFragments =
    [
        "secret",
        "password",
        "token",
        "apikey",
        "api_key",
        "privatekey",
        "private_key",
        "credential",
        "webhook",
        "signature",
        "authorization",
        "bearer",
        "clientsecret",
        "client_secret",
    ];

    public static bool IsSensitiveKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var normalized = key.Replace("-", string.Empty).Replace("_", string.Empty).ToLowerInvariant();
        return SensitiveKeyFragments.Any(fragment =>
            normalized.Contains(fragment.Replace("_", string.Empty), StringComparison.Ordinal));
    }

    public static IReadOnlyDictionary<string, string?>? Redact(
        IReadOnlyDictionary<string, string?>? summary)
    {
        if (summary is null || summary.Count == 0) return summary;

        return summary.ToDictionary(
            entry => entry.Key,
            entry => IsSensitiveKey(entry.Key) ? RedactedPlaceholder : entry.Value);
    }
}
