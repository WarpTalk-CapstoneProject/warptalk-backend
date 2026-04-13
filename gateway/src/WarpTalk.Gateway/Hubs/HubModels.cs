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
    float Confidence,
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
    int DurationMs);

public record AiAssistantResultDto(
    string TranslationRoomId,
    string Type,
    string Content,
    DateTime CreatedAt);

public record TranslationTextDto(
    string SegmentId,
    Guid SpeakerId,
    string OriginalText,
    string TranslatedText,
    string SourceLang,
    string TargetLang);
