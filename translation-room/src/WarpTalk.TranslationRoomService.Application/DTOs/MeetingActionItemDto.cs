using System;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

/// <summary>
/// One commitment from an approved biên bản.
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
    Guid SourceMinutesId,
    string Task,
    string? OwnerName,
    Guid? OwnerParticipantId,
    Guid? AssigneeUserId,
    long? AtMs,
    string Status,
    DateOnly? DueDate,
    DateTime? ClosedAt,
    DateTime CreatedAt);

public record UpdateActionItemStatusRequest(string Status, DateOnly? DueDate);
