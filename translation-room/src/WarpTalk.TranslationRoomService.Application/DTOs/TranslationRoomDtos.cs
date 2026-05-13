using System.ComponentModel.DataAnnotations;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

public record CreateTranslationRoomRequest(
    Guid? WorkspaceId,
    [MaxLength(255)] string Title,
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
    [MaxLength(255)] string Title,
    string? Description,
    [StringLength(12)] string TranslationRoomCode,
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
    TranslationRoomParticipantRole Role,
    string ListenLanguage,
    string SpeakLanguage,
    TranslationRoomParticipantStatus Status,
    DateTime? JoinedAt
);
