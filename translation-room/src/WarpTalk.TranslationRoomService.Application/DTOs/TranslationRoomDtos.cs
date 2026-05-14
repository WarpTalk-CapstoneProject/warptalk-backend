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
    [Required] [StringLength(12)] string TranslationRoomCode,
    [Required] [MaxLength(100)] string DisplayName,
    [Required] string ListenLanguage,
    [Required] string SpeakLanguage
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
    string SourceLanguage,
    string TargetLanguages,
    DateTime? ScheduledAt,
    DateTime? StartedAt,
    DateTime? EndedAt,
    DateTime CreatedAt
);

public record JoinTranslationRoomResponse(
    TranslationRoomDto Room,
    TranslationRoomParticipantDto Participant
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
