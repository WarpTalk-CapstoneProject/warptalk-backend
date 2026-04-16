using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

public record CreateTranslationRoomRequest(
    Guid? WorkspaceId,
    string Title,
    string? Description,
    TranslationRoomType TranslationRoomType, // e.g., Instant, Scheduled
    int MaxParticipants,
    string SourceLanguage,
    string TargetLanguages,
    DateTime? ScheduledAt
);

public record JoinTranslationRoomRequest(
    string DisplayName,
    string ListenLanguage,
    string SpeakLanguage
);

public record TranslationRoomDto(
    Guid Id,
    Guid WorkspaceId,
    Guid HostId,
    string Title,
    string? Description,
    string TranslationRoomCode,
    string Status,
    TranslationRoomType TranslationRoomType,
    int MaxParticipants,
    DateTime? ScheduledAt,
    DateTime? StartedAt,
    DateTime? EndedAt,
    DateTime CreatedAt
);

public record TranslationRoomParticipantDto(
    Guid Id,
    Guid TranslationRoomId,
    Guid UserId,
    string DisplayName,
    string Role,
    string ListenLanguage,
    string SpeakLanguage,
    string Status,
    DateTime? JoinedAt
);
