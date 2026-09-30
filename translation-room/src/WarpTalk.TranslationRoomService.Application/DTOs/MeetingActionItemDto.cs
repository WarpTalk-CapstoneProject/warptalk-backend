using System;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

/// <summary>
/// One commitment from an approved biên bản, or one somebody asked WarpBot for (see
/// <see cref="Source"/>).
///
/// Both <see cref="OwnerName"/> and <see cref="OwnerParticipantId"/> travel: the first is what the
/// meeting said and always renders, the second is who that turned out to be and is null whenever
/// the name was ambiguous or matched nobody. A client showing only the second would make an
/// unresolved owner disappear from a line that clearly names one.
/// </summary>
public record MeetingActionItemDto(
    Guid Id,
    Guid TranslationRoomId,
    string? RoomTitle,
    Guid? SourceMinutesId,
    string Task,
    string? OwnerName,
    Guid? OwnerParticipantId,
    Guid? AssigneeUserId,
    long? AtMs,
    string Status,
    DateOnly? DueDate,
    DateTime? ClosedAt,
    DateTime CreatedAt,
    // Appended, never inserted: positional records are constructed by position, and a field slid
    // into the middle shifts every argument after it at every call site that still compiles.
    string Source = "MINUTES");

public record UpdateActionItemStatusRequest(string Status, DateOnly? DueDate);

/// <summary>
/// A task somebody asked for in so many words — today through WarpBot.
///
/// OWNER
///     <see cref="AssignToSelf"/> is how "owner: me" arrives, and it wins: the caller is the one
///     person this service never has to guess at. Otherwise <see cref="OwnerName"/> is matched
///     against the meeting's participants by the same conservative rule approval uses
///     (ActionItemOwnerResolver), and a name that does not match exactly one person is REFUSED
///     rather than stored unassigned — the person asking is still there to be asked who they
///     meant, which a signed document never is. Neither set means the task has no owner yet.
///
/// DUE DATE
///     Optional. "Deadline: not decided" is a real answer and is stored as NULL, never as today.
/// </summary>
public record CreateActionItemRequest(
    string Task,
    bool AssignToSelf = false,
    string? OwnerName = null,
    DateOnly? DueDate = null);
