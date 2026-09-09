using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

/// <summary>
/// Reading and closing the work a meeting produced.
///
/// WHO MAY CLOSE ONE
///     The person it was assigned to, or the meeting host. Not "anyone who can read the meeting":
///     a task list where a bystander can tick somebody else's work off is a task list nobody
///     trusts. The host is included because an unassigned or wrongly-assigned task still has to be
///     closeable by somebody, and the host is who the meeting made answerable for its record.
/// </summary>
public interface IMeetingActionItemService
{
    Task<Result<List<MeetingActionItemDto>>> GetForRoomAsync(
        Guid roomId, Guid userId, string? userEmail, CancellationToken ct = default);

    /// <summary>One person's own outstanding work across every meeting in a workspace.</summary>
    Task<Result<List<MeetingActionItemDto>>> GetMineAsync(
        Guid workspaceId, Guid userId, string? status, CancellationToken ct = default);

    Task<Result<MeetingActionItemDto>> UpdateStatusAsync(
        Guid itemId, Guid userId, string status, DateOnly? dueDate, CancellationToken ct = default);
}
