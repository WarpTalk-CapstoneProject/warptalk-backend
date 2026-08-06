using System;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

/// <summary>
/// Participant projection for server-to-server lookups. Deliberately separate from
/// <see cref="TranslationRoomParticipantDto"/>: that one is the authenticated,
/// user-facing shape and declares a non-nullable UserId, while guests legitimately
/// have none.
/// </summary>
/// <remarks>
/// WT-263: Status is carried through as the stored string rather than pre-reduced to a boolean.
/// "Present in the room" has exactly one definition — <c>TranslationRoomParticipantStatuses.HoldsSeat</c>
/// — and the capacity cap already reads it; projecting a private bool here would be a second copy of
/// that rule, free to drift from the one the cap enforces.
/// </remarks>
public record TranslationRoomParticipantSummaryDto(
    Guid? UserId,
    string DisplayName,
    string Role,
    string SpeakLanguage,
    string Status
);
