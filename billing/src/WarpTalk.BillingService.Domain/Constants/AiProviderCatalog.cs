using System;
using System.Collections.Generic;

namespace WarpTalk.BillingService.Domain.Constants;

/// <summary>
/// Which AI provider serves each charge type, for the profit-and-loss report's per-provider split.
///
/// The rate card a consume row was settled on names its provider, and that wins. This map is only
/// the fallback for a row with no card (older rows, internal CRD cards without a provider), taken
/// from the registered billing identities in UsageRateCardAdminService: dubbing and voice cloning
/// are Cartesia; speech-to-text (gpt-4o-transcribe), translation and the assistant are OpenAI.
/// </summary>
public static class AiProviderCatalog
{
    public const string Cartesia = "cartesia";
    public const string OpenAi = "openai";
    public const string Unknown = "unknown";

    private static readonly IReadOnlyDictionary<string, string> ByChargeType =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AUDIO_DUBBING_STANDARD"] = Cartesia,
            ["AUDIO_DUBBING_VOICE_CLONE"] = Cartesia,
            ["VOICE_CLONE_ENROLLMENT"] = Cartesia,
            ["STT"] = OpenAi,
            ["TRANSLATION"] = OpenAi,
            ["AI_ASSISTANT"] = OpenAi,
            ["AI_SUMMARY"] = OpenAi,
            // Lower-case charge types the older C# path wrote.
            ["voice_translation"] = OpenAi,
            ["text_to_speech"] = Cartesia,
            ["chat"] = OpenAi,
        };

    /// <summary>The card's provider (lower-cased) when it names one, else the charge type's, else <see cref="Unknown"/>.</summary>
    public static string Resolve(string? cardProvider, string? chargeType)
    {
        if (!string.IsNullOrWhiteSpace(cardProvider)) return cardProvider.Trim().ToLowerInvariant();
        return chargeType is not null && ByChargeType.TryGetValue(chargeType.Trim(), out var provider) ? provider : Unknown;
    }

    /// <summary>What an operator calls the service a charge type buys.</summary>
    public static string ServiceOf(string chargeType) => chargeType.Trim().ToUpperInvariant() switch
    {
        "STT" => "STT",
        "TRANSLATION" or "VOICE_TRANSLATION" => "MT",
        "AUDIO_DUBBING_STANDARD" or "AUDIO_DUBBING_VOICE_CLONE" or "TEXT_TO_SPEECH" => "TTS",
        "VOICE_CLONE_ENROLLMENT" => "Voice clone",
        "AI_ASSISTANT" or "AI_SUMMARY" or "CHAT" => "LLM",
        _ => "Other",
    };
}
