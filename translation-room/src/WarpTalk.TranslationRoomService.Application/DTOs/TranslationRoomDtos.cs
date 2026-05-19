using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record RoomSettingsRequest(
    bool RequiresApproval = true
);

public record RoomSettingsResponse(
    bool RequiresApproval
);

public record UpdateRoomSettingsRequest(
    RoomSettingsRequest? Settings,
    string? SourceLanguage,
    List<string>? TargetLanguages
);

public record GetTranslationRoomsRequest(
    string? Status = null,
    string? Search = null,
    DateTime? From = null,
    DateTime? To = null,
    int Page = 1,
    int PageSize = 20
);

public record CreateTranslationRoomRequest(
    Guid? WorkspaceId,
    [Required] string Title,
    string? Description,
    TranslationRoomType TranslationRoomType, // e.g., Instant, Scheduled
    int MaxParticipants,
    string? SourceLanguage,
    List<string>? TargetLanguages,
    RoomSettingsRequest? Settings,
    DateTime? ScheduledAt
);

public record JoinTranslationRoomRequest(
    [Required] string TranslationRoomCode,
    [Required] string DisplayName,
    string? SpeakLanguage,
    string? ListenLanguage
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
    List<string> TargetLanguages,
    DateTime? ScheduledAt,
    DateTime? StartedAt,
    DateTime? EndedAt,
    int? DurationSeconds,
    DateTime CreatedAt,
    RoomSettingsResponse Settings
);

public record TranslationRoomListItemDto(
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
    List<string> TargetLanguages,
    DateTime? ScheduledAt,
    DateTime? StartedAt,
    DateTime? EndedAt,
    int? DurationSeconds,
    DateTime CreatedAt,
    RoomSettingsResponse Settings,
    int ParticipantCount,
    bool IsHost
);

public record TranslationRoomListResponse(
    List<TranslationRoomListItemDto> Rooms,
    int Total,
    int Page,
    int PageSize
);

public record JoinTranslationRoomResponse(
    TranslationRoomDto Room,
    TranslationRoomParticipantDto Participant
);

public record TranslationRoomArtifactDto(
    Guid Id,
    Guid TranslationRoomId,
    string Type,
    string Title,
    string? FileUrl,
    string? FileFormat,
    long? FileSizeBytes,
    bool ContainsRawAudio,
    bool ContainsRawVideo,
    bool ConsentRequired,
    DateTime? RetentionUntil,
    string Status,
    DateTime CreatedAt
);

public record TranslationRoomHistoryItemDto(
    TranslationRoomListItemDto Room,
    List<TranslationRoomParticipantDto> Participants,
    List<TranslationRoomArtifactDto> Artifacts
);

public record TranslationRoomHistoryResponse(
    List<TranslationRoomHistoryItemDto> Rooms,
    int Total,
    int Page,
    int PageSize
);

public record SubmitTranslationRoomFeedbackRequest(
    [Range(1, 5)] int OverallRating,
    [Range(1, 5)] int? TranslationQuality,
    [Range(1, 5)] int? AudioQuality,
    [Range(1, 5)] int? VoiceCloneQuality,
    [Range(1, 5)] int? AiSummaryQuality,
    string? Comments,
    Dictionary<string, object>? CommunicationInsights
);

public record TranslationRoomFeedbackDto(
    Guid Id,
    Guid TranslationRoomId,
    Guid UserId,
    int OverallRating,
    int? TranslationQuality,
    int? AudioQuality,
    int? VoiceCloneQuality,
    int? AiSummaryQuality,
    string? Comments,
    Dictionary<string, object>? CommunicationInsights,
    DateTime CreatedAt
);

public record TranslationRoomFeedbackStateDto(
    bool HasSubmitted,
    TranslationRoomFeedbackDto? Feedback
);
