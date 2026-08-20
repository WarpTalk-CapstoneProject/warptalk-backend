using WarpTalk.TranslationRoomService.Domain.Entities;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.TranslationRoomService.Domain.Interfaces;

public interface IMeetingActionItemRepository : IGenericRepository<MeetingActionItem>
{
    Task<List<MeetingActionItem>> GetByRoomIdAsync(Guid roomId, CancellationToken ct = default);

    /// <summary>Whether this minutes version has already produced its tasks — the idempotency check.</summary>
    Task<bool> AnyForMinutesAsync(Guid minutesId, CancellationToken ct = default);

    /// <summary>
    /// What earlier occurrences of a recurring booking left open, newest first, excluding the
    /// room now being written up. This is the carry-over query.
    /// </summary>
    Task<List<MeetingActionItem>> GetOpenForSeriesAsync(
        Guid seriesId, Guid excludingRoomId, CancellationToken ct = default);

    /// <summary>One person's outstanding work across every meeting in a workspace.</summary>
    Task<List<MeetingActionItem>> GetForAssigneeAsync(
        Guid workspaceId, Guid userId, string? status, CancellationToken ct = default);
}
