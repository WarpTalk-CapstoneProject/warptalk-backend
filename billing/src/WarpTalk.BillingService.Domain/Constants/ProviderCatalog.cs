using System;
using System.Collections.Generic;
using System.Linq;

namespace WarpTalk.BillingService.Domain.Constants;

/// <summary>
/// The external providers the admin Providers page reports on, and what WarpTalk uses each for.
///
/// Only providers production actually calls are listed (warptalk-infrastructure chart values:
/// STT_MODEL gpt-live-transcribe, TRANSLATION_MODEL gpt-4.1, ASSISTANT_MODEL gpt-5.6-luna,
/// EMBEDDING_MODEL text-embedding-3-small, TTS_MODEL sonic-3.5, LIVEKIT_URL, Stripe:SecretKey).
/// A provider WarpTalk hosts itself (Qdrant, MinIO, Redis, Postgres) is infrastructure, not a
/// provider, and has its own Grafana dashboards.
/// </summary>
public static class ProviderCatalog
{
    public const string OpenAi = "openai";
    public const string Cartesia = "cartesia";
    public const string LiveKit = "livekit";
    public const string Stripe = "stripe";

    public static class Categories
    {
        public const string Ai = "ai";
        public const string Media = "media";
        public const string Payments = "payments";
    }

    /// <summary>What a provider's headline usage counts.</summary>
    public static class UsageUnits
    {
        /// <summary>WarpTalk credits charged to customers for the provider's charge types (the ledger).</summary>
        public const string Credits = "credits";
        /// <summary>Credits the provider itself reports it charged WarpTalk (Cartesia: ~1 per character).</summary>
        public const string ProviderCredits = "providerCredits";
        public const string ParticipantMinutes = "participantMinutes";
        public const string Payments = "payments";
    }

    public sealed record ProviderInfo(
        string Key,
        string Name,
        string Category,
        IReadOnlyList<string> Services,
        string UsageUnit);

    public static readonly IReadOnlyList<ProviderInfo> All =
    [
        new(OpenAi, "OpenAI", Categories.Ai,
            ["Speech-to-text", "Translation", "WarpBot assistant", "Summaries & minutes", "Embeddings", "Content safety"],
            UsageUnits.Credits),
        new(Cartesia, "Cartesia", Categories.Ai, ["Dubbing (TTS)", "Voice cloning"], UsageUnits.ProviderCredits),
        new(LiveKit, "LiveKit", Categories.Media, ["Real-time audio/video", "Recording egress"], UsageUnits.ParticipantMinutes),
        new(Stripe, "Stripe", Categories.Payments, ["Card payments", "Subscriptions", "Credit top-ups"], UsageUnits.Payments),
    ];

    public static ProviderInfo? Find(string? key)
        => key is null ? null : All.FirstOrDefault(p => string.Equals(p.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
}
