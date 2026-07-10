using System;
using System.Collections.Generic;

namespace WarpTalk.AuthService.Application.DTOs;

public record VoiceProfileDto(
    Guid Id,
    Guid UserId,
    Guid? WorkspaceId,
    string? DisplayName,
    string? Provider,
    string? EmbeddingRef,
    string Status,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<VoiceSampleDto> Samples,
    IReadOnlyList<VoiceConsentDto> Consents
);

public record VoiceSampleDto(
    Guid Id,
    Guid VoiceProfileId,
    string SampleType,
    string? FileUrl,
    int? DurationSeconds,
    string? Language,
    bool ContainsRawAudio,
    DateTime? RetentionUntil,
    DateTime CreatedAt
);

public record VoiceConsentDto(
    Guid Id,
    Guid UserId,
    Guid? VoiceProfileId,
    string ConsentType,
    string ConsentStatus,
    string ConsentTextVersion,
    DateTime? GrantedAt,
    DateTime? RevokedAt,
    DateTime CreatedAt
);

public record CreateVoiceProfileRequest(
    string? DisplayName,
    Guid? WorkspaceId = null,
    string? Provider = null
);

public record UpdateVoiceProfileRequest(
    string? DisplayName = null,
    string? Provider = null,
    string? EmbeddingRef = null,
    string? Status = null
);

public record AddVoiceSampleRequest(
    string SampleType,
    string FileUrl,
    int? DurationSeconds = null,
    string? Language = null,
    bool ContainsRawAudio = true,
    DateTime? RetentionUntil = null
);

public record GrantVoiceConsentRequest(
    string ConsentType,
    string ConsentTextVersion
);

public record RevokeVoiceConsentRequest(
    string ConsentType,
    string ConsentTextVersion
);
