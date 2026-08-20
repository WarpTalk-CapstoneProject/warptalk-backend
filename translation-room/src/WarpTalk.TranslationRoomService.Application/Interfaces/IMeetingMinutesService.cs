using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

/// <summary>
/// The lifecycle of a biên bản họp: drawn up, edited, signed by the secretary, approved by the
/// chair, and thereafter immutable.
///
/// WHO MAY DO WHAT
///     Reading follows the room — anyone who can see the meeting can read its minutes, because a
///     minutes document that only its author can read is not a record of anything.
///     Every write is host authority. The host IS the secretary and the chair in this product
///     (decided 2026-08-20), so there is no separate role to grant; the two participant columns on
///     the row exist so a later product that separates them does not need a migration.
///
/// WHY APPROVAL IS A WALL AND NOT A FLAG
///     Once approved, the row is never written again. A correction becomes version N+1 with the
///     approved one kept and demoted from <c>IsCurrent</c>. This is how an organisation handles a
///     record somebody signed: you do not edit it, you issue a revision — and both stay readable,
///     which is the only way a reader can tell what was actually agreed at the time.
/// </summary>
public interface IMeetingMinutesService
{
    Task<Result<MeetingMinutesDto>> GetCurrentAsync(
        Guid roomId, Guid userId, string? userEmail, CancellationToken ct = default);

    /// <summary>
    /// Draw up the draft from the meeting's own record. Idempotent while one is still unapproved:
    /// pressing twice returns the document that already exists rather than renumbering the meeting.
    /// </summary>
    Task<Result<MeetingMinutesDto>> CreateDraftAsync(
        Guid roomId, Guid userId, CancellationToken ct = default);

    Task<Result<MeetingMinutesDto>> UpdateContentAsync(
        Guid roomId, Guid minutesId, Guid userId, string contentJson, CancellationToken ct = default);

    /// <summary>The secretary takes responsibility for the content. Records how much they changed.</summary>
    Task<Result<MeetingMinutesDto>> SignAsync(
        Guid roomId, Guid minutesId, Guid userId, CancellationToken ct = default);

    Task<Result<MeetingMinutesDto>> ApproveAsync(
        Guid roomId, Guid minutesId, Guid userId, CancellationToken ct = default);

    /// <summary>Open version N+1 from an approved document, leaving the signed one on record.</summary>
    Task<Result<MeetingMinutesDto>> ReviseAsync(
        Guid roomId, Guid minutesId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// The minutes as a .docx, for anyone who can read the meeting.
    ///
    /// Read authority, not host authority: a biên bản exists to be circulated to the people who
    /// were at the meeting, and one only its author can download is a record of nothing. The
    /// document prints its own status, so a draft that leaves the building says it is a draft.
    /// </summary>
    Task<Result<MinutesExportFile>> ExportDocxAsync(
        Guid roomId, Guid userId, string? userEmail, CancellationToken ct = default);
}
