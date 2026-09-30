using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace WarpTalk.BillingService.Domain.Constants;

/// <summary>Vocabulary of subscription.provider_usage_daily and of the Cartesia usage sync.</summary>
public static class ProviderUsageConstants
{
    public static class Providers
    {
        public const string Cartesia = "cartesia";
    }

    public static class GroupKinds
    {
        /// <summary>Group id <see cref="TotalGroupId"/>; one row per synced day, even at 0 credits.</summary>
        public const string Total = "total";
        public const string Capability = "capability";
        public const string Model = "model";
    }

    public const string TotalGroupId = "all";

    /// <summary>
    /// billing_pricing_config key: USD per Cartesia credit. The account is on Cartesia's Free plan but
    /// spends a stock of credits bought earlier, so the marginal price is 0 — margin reporting still
    /// has to charge what those credits cost. Default: the Startup plan, $49 / 1,250,000 credits.
    /// </summary>
    public const string CartesiaUsdPerCreditConfigKey = "cartesia_usd_per_credit";

    public const decimal DefaultCartesiaUsdPerCredit = 0.0000392m;

    /// <summary>
    /// The charge types WarpTalk serves with Cartesia TTS. Their provider cost comes from measured
    /// Cartesia credits wherever the sync covers the day.
    /// </summary>
    public static readonly IReadOnlySet<string> CartesiaChargeTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "AUDIO_DUBBING_STANDARD",
        "AUDIO_DUBBING_VOICE_CLONE",
        "VOICE_CLONE_ENROLLMENT",
    };

    private static readonly Regex SpeechToText = new(
        @"(^|[^a-z])(stt|ink)([^a-z]|$)|speech[-_ ]?to[-_ ]?text|transcri",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// True for a Cartesia capability that is speech-to-text (Ink). Production STT is not Cartesia,
    /// so such credits (probes, experiments) must not be charged to dubbing. Everything else on the
    /// account — TTS, infill, voice cloning — is dubbing's cost.
    /// </summary>
    public static bool IsSpeechToTextCapability(string id, string? label)
        => SpeechToText.IsMatch(id) || (label is not null && SpeechToText.IsMatch(label));

    /// <summary>What <c>status</c> the Cartesia sync reports.</summary>
    public static class SyncStatuses
    {
        public const string Ok = "ok";
        public const string Disabled = "disabled";
        public const string Error = "error";
        /// <summary>Configured, but the first sync since start has not finished.</summary>
        public const string Pending = "pending";
    }
}
