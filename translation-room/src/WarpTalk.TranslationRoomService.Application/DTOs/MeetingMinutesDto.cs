using System;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

/// <summary>
/// One biên bản as the web reads it.
///
/// <c>Content</c> stays a JSON string rather than the parsed object: the secretary's editor sends
/// it back verbatim, and re-serialising through a typed model on every read would silently drop
/// any field the server does not yet know about — including ones a newer web has started writing.
/// </summary>
public record MeetingMinutesDto(
    Guid Id,
    Guid TranslationRoomId,
    string MinutesNo,
    string Status,
    int Version,
    bool IsCurrent,
    Guid? PreviousMinutesId,
    int? BasedOnTranscriptVersion,
    string? DraftedByEngine,
    DateTime? DraftedAt,
    Guid? SecretaryParticipantId,
    string? SecretaryName,
    DateTime? SecretarySignedAt,
    Guid? ChairParticipantId,
    string? ChairName,
    DateTime? ChairApprovedAt,
    int EditCountVsDraft,
    string Content,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record UpdateMinutesContentRequest(string Content);
