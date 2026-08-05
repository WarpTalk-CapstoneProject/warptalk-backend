using System;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

/// <summary>
/// Participant projection for server-to-server lookups. Deliberately separate from
/// <see cref="TranslationRoomParticipantDto"/>: that one is the authenticated,
/// user-facing shape and declares a non-nullable UserId, while guests legitimately
/// have none.
/// </summary>
public record TranslationRoomParticipantSummaryDto(
    Guid? UserId,
    string DisplayName,
    string Role,
    string SpeakLanguage,
    bool IsConnected
);
