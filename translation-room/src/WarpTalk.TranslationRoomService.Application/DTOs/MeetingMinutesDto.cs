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

/// <summary>A rendered minutes file, ready to be handed to the browser.</summary>
public record MinutesExportFile(byte[] Bytes, string FileName, string ContentType);

/// <summary>
/// One row of a workspace's minutes library.
///
/// Carries just enough of the meeting to LIST the document without a second call — its title, its
/// code, when it ended. Not the whole room: the library page already holds the meeting record for
/// every room it lists, and a second copy of it here would be a second thing to keep in step.
/// </summary>
public record WorkspaceMinutesItemDto(
    MeetingMinutesDto Minutes,
    string RoomTitle,
    string RoomCode,
    Guid RoomHostId,
    string RoomStatus,
    DateTime? RoomEndedAt);

/// <summary>
/// Paged, with the server's own <c>Total</c> — the same shape <c>TranslationRoomHistoryResponse</c>
/// uses, so the two lists the library page merges page the same way.
/// </summary>
public record WorkspaceMinutesResponse(
    List<WorkspaceMinutesItemDto> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>
/// Filters for the minutes library.
///
/// <c>PageSize</c> defaults well below the room history's 100 because every row carries its whole
/// <c>Content</c> document, which is what makes searching and previewing the library possible
/// without a fetch per card — and what makes a large page expensive.
/// </summary>
public record GetWorkspaceMinutesRequest(
    string? Search = null,
    string? Status = null,
    int Page = 1,
    int PageSize = 50);
