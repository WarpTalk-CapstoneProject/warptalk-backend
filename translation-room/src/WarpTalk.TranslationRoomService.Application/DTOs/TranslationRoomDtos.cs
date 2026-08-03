using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

/// <summary>
/// Explicit room settings. Every field is nullable so "not sent" stays distinguishable from
/// "sent false" — the meeting type seeds anything left unset (see TranslationRoomTypePolicy),
/// and a non-null value here always wins over the type's default.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record RoomSettingsRequest(
    bool? RequiresApproval = null,
    string? ArtifactAccess = null,
    bool? MuteOnEntry = null,
    bool? AutoRecord = null,
    bool? BreakoutsEnabled = null
);

public record RoomSettingsResponse(
    bool RequiresApproval,
    string ArtifactAccess,
    bool MuteOnEntry,
    bool AutoRecord,
    bool BreakoutsEnabled
);

public record UpdateRoomSettingsRequest(
    string? Title,
    string? Description,
    int? MaxParticipants,
    DateTime? ScheduledAt,
    List<string>? InvitedEmails,
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
    int PageSize = 20,
    Guid? WorkspaceId = null
);

public record CreateTranslationRoomRequest(
    Guid? WorkspaceId,
    [Required] string Title,
    string? Description,
    string TranslationRoomType, // one of TranslationRoomTypes
    // Optional: omit to let the meeting type decide the seat count.
    int? MaxParticipants,
    string? SourceLanguage,
    List<string>? TargetLanguages,
    RoomSettingsRequest? Settings,
    DateTime? ScheduledAt,
    List<string>? InvitedEmails
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
    RoomStatus Status,
    string TranslationRoomType,
    int MaxParticipants,
    string SourceLanguage,
    List<string> TargetLanguages,
    DateTime? ScheduledAt,
    List<string>? InvitedEmails,
    DateTime? StartedAt,
    DateTime? EndedAt,
    int? DurationSeconds,
    DateTime CreatedAt,
    RoomSettingsResponse Settings,
    List<RoomArtifactDto>? Artifacts = null
);

public record TranslationRoomListItemDto(
    Guid Id,
    Guid WorkspaceId,
    Guid HostId,
    [MaxLength(255)] string Title,
    string? Description,
    [StringLength(12)] string TranslationRoomCode,
    RoomStatus Status,
    string TranslationRoomType,
    int MaxParticipants,
    string SourceLanguage,
    List<string> TargetLanguages,
    DateTime? ScheduledAt,
    List<string>? InvitedEmails,
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

public record RoomArtifactDto(
    Guid Id,
    string ArtifactType,
    string? FileFormat,
    long? FileSizeBytes,
    bool ContainsRawAudio,
    bool ContainsRawVideo,
    bool ConsentRequired,
    DateTime? RetentionUntil,
    string Status,
    DateTime CreatedAt
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
    DateTime CreatedAt,
    // WT-13: inline payload (e.g. AI meeting-summary JSON) for artifact types that don't
    // need external file storage.
    string? Content = null
);

public record CreateArtifactRequest(
    Guid RoomId,
    string ArtifactType,
    string? FileUrl,
    string FileFormat,
    long SizeBytes,
    bool ContainsRawAudio,
    bool ContainsRawVideo,
    bool ConsentRequired,
    DateTime? RetentionUntil = null,
    string? Content = null
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
