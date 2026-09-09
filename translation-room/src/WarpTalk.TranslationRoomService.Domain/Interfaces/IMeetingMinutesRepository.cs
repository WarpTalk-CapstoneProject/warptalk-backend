using WarpTalk.TranslationRoomService.Domain.Entities;

using System;
using System.Linq.Expressions;
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
    /// index on (workspace_id, minutes_no, version) rejects the loser — which is the intended
    /// outcome, not a flaw in the count: a duplicated minutes number is worse than a retry.
    ///
    /// Counts DOCUMENTS, not rows: a revision keeps the number of the document it revises, so
    /// counting its row too would skip the next number and leave a gap in the register.
    /// </summary>
    /// <summary>
    /// The workspace library's search clause: minutes number, the DOCUMENT's own title, or the
    /// room's code.
    ///
    /// Deliberately the title inside <c>content</c> and not <c>TranslationRoom.Title</c>. The two
    /// diverge the moment somebody renames a room, and this is a library of DOCUMENTS — searching
    /// it by what the room is called today finds records whose own front page says something else.
    ///
    /// Returned as an expression from the repository rather than written in the service because
    /// reaching into a jsonb column takes a provider-specific function, and the Application layer
    /// does not reference the database vendor. Same arrangement as
    /// <c>RoomReadAccess.IsReadableBy</c>, for the same reason: one definition, translated to SQL.
    /// </summary>
    Expression<Func<MeetingMinutes, bool>> MatchesSearch(string search);

    Task<int> CountForWorkspaceYearAsync(Guid workspaceId, int year, CancellationToken ct = default);
}
