using System;

namespace WarpTalk.TranscriptService.Domain.Entities;

/// <summary>
/// One synthesized-audio result for a TranslationContent, deduplicated on
/// (workspace_id, text_hash, provider_voice_id) via audio_dubbings_dedup_idx.
/// Voice fields are intentionally denormalized (not a voice_profile_id FK) — see migration
/// 017-15-07-2026-translation-cluster-finalize.sql STEP 4: tts_worker's voice cloning is
/// ephemeral/provider-driven (Cartesia mints a voice_id per speaker per room, cached only in
/// Redis with a TTL), so there is nothing durable to point a FK at.
/// </summary>
public partial class AudioDubbing
{
    public Guid Id { get; set; }

    /// <summary>External AuthService workspace id. No physical FK.</summary>
    public Guid WorkspaceId { get; set; }

    public Guid TranslationContentId { get; set; }

    /// <summary>MD5 of the translated text actually synthesized — matches TranslationContent.TextHash's role but keyed to the synthesized text, not the source.</summary>
    public string TextHash { get; set; } = null!;

    /// <summary>'cloned' | 'default' — matches TTSResultMessage.voice_type exactly.</summary>
    public string VoiceType { get; set; } = null!;

    /// <summary>Matches TTSResultMessage.clone_provider/anchor_provider.</summary>
    public string Provider { get; set; } = "cartesia";

    /// <summary>Raw provider voice id actually used — always populated, even for VoiceType == "default" (see CartesiaSynthesizer._default_voice_id). The real dedup disambiguator, not VoiceType.</summary>
    public string ProviderVoiceId { get; set; } = null!;

    public Guid? PreviousAudioDubbingId { get; set; }

    public string? AudioUrl { get; set; }

    public int? DurationMs { get; set; }

    /// <summary>Claim-state: pending | processing | done | failed.</summary>
    public string Status { get; set; } = "pending";

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual TranslationContent TranslationContent { get; set; } = null!;

    public virtual AudioDubbing? PreviousAudioDubbing { get; set; }
}
