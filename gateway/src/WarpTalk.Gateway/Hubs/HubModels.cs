namespace WarpTalk.Gateway.Hubs;

// ── TranslationRoom Hub DTOs ──────────────────────────────────────

public record ParticipantInfoDto(
    Guid UserId,
    string DisplayName,
    string SpeakLanguage,
    string ListenLanguage,
    bool IsMuted,
    DateTime JoinedAt);

public record TranscriptSegmentDto(
    Guid SegmentId,
    Guid SpeakerId,
    string SpeakerName,
    string OriginalText,
    string OriginalLanguage,
    string? TranslatedText,
    string? TargetLanguage,
    // WT-277: nullable. `null` means the STT worker reported no confidence for this segment (no
    // logprobs on the event, or an unparsable field) — it used to be sent as 1.0f, so the client
    // could not tell "unknown" from "maximally confident". Clients must render null as unknown,
    // not as a score.
    float? Confidence,
    int StartTimeMs,
    int EndTimeMs);

public record ChatMessageDto(
    Guid MessageId,
    Guid SenderId,
    string SenderName,
    string Content,
    DateTime SentAt);

public record TranslationRoomStateDto(
    Guid TranslationRoomId,
    string TranslationRoomCode,
    string Status,
    List<ParticipantInfoDto> Participants);

// ── Notification Hub DTOs ─────────────────────────────────

public record NotificationDto(
    Guid NotificationId,
    string Type,
    string Title,
    string Body,
    string Priority,
    object? Data,
    DateTime CreatedAt);

// ── AI Pipeline Result DTOs ───────────────────────────────

public record TranslatedAudioDto(
    string SegmentId,
    Guid SpeakerId,
    string AudioBase64,
    string VoiceType,
    int DurationMs,
    string? VoiceMode = null,
    double? CloneStrength = null,
    string? AnchorProvider = null,
    string? CloneProvider = null,
    string? RenderLocation = null,
    string? CacheKey = null,
    bool? CacheHit = null,
    int? SynthesisLatencyMs = null,
    int? ConversionLatencyMs = null,
    string? FallbackReason = null);

public record AiAssistantResultDto(
    string TranslationRoomId,
    string Type,
    string Content,
    DateTime CreatedAt);

/// <summary>
/// An unprompted one-line hint about a live transcript segment, produced by
/// warptalk-ai/suggestion_worker and delivered as "AiSuggestionReceived".
///
/// Shares the ai_assistant:results stream with AiAssistantResultDto — the stream's
/// `type` field is what separates them, so a suggestion never reaches the summary /
/// action-items path and vice versa.
///
/// SegmentId is the STT segment that triggered the hint. The client anchors the strip to
/// the transcript bubble containing that id; because the client merges consecutive
/// segments from one speaker into a single bubble, that is not necessarily the bubble's
/// own id. Ephemeral — nothing about this is persisted anywhere.
/// </summary>
public record AiSuggestionDto(
    string TranslationRoomId,
    string SegmentId,
    string Category,
    string Content,
    string? Detail,
    float Confidence,
    string Language,
    DateTime CreatedAt);

/// <summary>
/// A translated caption chunk pushed as "TranslationTextReceived".
/// </summary>
/// <remarks>
/// WT-278: this record deliberately carries NO confidence field. warptalk-ai's translator emits no
/// quality score of its own; the only number available is the source STT segment's avg_logprob,
/// which measures the audio, not the translation. Adding it here would put a number that does not
/// describe the translation onto a translation payload — exactly the defect WT-278 removed. If a
/// real translation quality signal is ever built, give it its own explicitly named field.
/// </remarks>
public record TranslationTextDto(
    string SegmentId,
    Guid SpeakerId,
    string OriginalText,
    string TranslatedText,
    string SourceLang,
    string TargetLang,
    int StartTimeMs = 0,
    int EndTimeMs = 0,
    // Links back to the TranscriptSegmentReceived bubble this translation belongs to —
    // see TranslationResultMessage.source_segment_id in translation_worker. Without this
    // the frontend can only guess the link from SegmentId's "-{lang}-c{idx}" suffix, which
    // silently fails to merge and creates a duplicate, timestamp-less bubble.
    string SourceSegmentId = "",
    int ChunkIndex = 0);

/// <summary>
/// One selectable TTS voice for the control bar's voice picker — see
/// TranslationRoomHub.GetVoiceCatalog. Id is a real Cartesia voice id (from
/// tts_worker's cached CartesiaSynthesizer.list_voices() result), safe to round-trip
/// straight back into SetVoicePreference.
/// </summary>
public record VoiceOptionDto(string Id, string Name, string Gender);
