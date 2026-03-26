namespace WarpTalk.Gateway.Hubs;

// ── Meeting Hub DTOs ──────────────────────────────────────

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

public record MeetingStateDto(
    Guid MeetingId,
    string MeetingCode,
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
