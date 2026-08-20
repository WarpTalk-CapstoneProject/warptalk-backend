using WarpTalk.TranslationRoomService.Domain.Entities;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.TranslationRoomService.Domain.Interfaces;

public interface IMeetingMinutesRepository : IGenericRepository<MeetingMinutes>
{
    /// <summary>The room's minutes of record, or null when none has been drawn up yet.</summary>
    Task<MeetingMinutes?> GetCurrentByRoomIdAsync(Guid roomId, CancellationToken ct = default);

    /// <summary>Every version for a room, newest first — the history of what was signed.</summary>
    Task<List<MeetingMinutes>> GetVersionsByRoomIdAsync(Guid roomId, CancellationToken ct = default);

    /// <summary>
    /// How many minutes a workspace has already numbered in a given year, so the next one can be
    /// <c>BB-{year}-{count + 1}</c>. Two callers racing produce the same number and the unique
    /// index on (workspace_id, minutes_no) rejects the loser — which is the intended outcome, not
    /// a flaw in the count: a duplicated minutes number is worse than a retry.
    /// </summary>
    Task<int> CountForWorkspaceYearAsync(Guid workspaceId, int year, CancellationToken ct = default);
}
