using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record RoomSettingsRequest(
    bool RequiresApproval = true,
    ArtifactAccessLevel HistoryAccess = ArtifactAccessLevel.HostOnly
);

public record RoomSettingsResponse(
    bool RequiresApproval,
    ArtifactAccessLevel HistoryAccess
);

public record UpdateRoomSettingsRequest(
    RoomSettingsRequest? Settings,
    string? SourceLanguage,
    List<string>? TargetLanguages
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
    DateTime CreatedAt,
    RoomSettingsResponse Settings,
    List<RoomArtifactDto>? Artifacts = null
);

public record JoinTranslationRoomResponse(
    TranslationRoomDto Room,
    TranslationRoomParticipantDto Participant
);

public record RoomArtifactDto(
    Guid Id,
    ArtifactType ArtifactType,
    string? FileFormat,
    long? FileSizeBytes,
    bool ContainsRawAudio,
    bool ContainsRawVideo,
    bool ConsentRequired,
    DateTime? RetentionUntil,
    string Status,
    DateTime CreatedAt
);

public record CreateArtifactRequest(
    Guid RoomId,
    ArtifactType ArtifactType,
    string FileUrl,
    string FileFormat,
    long SizeBytes,
    bool ContainsRawAudio,
    bool ContainsRawVideo,
    bool ConsentRequired,
    DateTime? RetentionUntil = null
);
