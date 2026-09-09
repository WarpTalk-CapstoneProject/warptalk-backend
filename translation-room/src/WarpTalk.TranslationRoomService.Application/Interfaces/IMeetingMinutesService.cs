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
    /// Every current biên bản in one workspace that this caller may read.
    ///
    /// The library page's reason to exist: minutes were reachable only at
    /// <c>rooms/{roomId}/minutes</c>, so finding one meant already knowing which meeting produced
    /// it. Answering "which meetings have a signed record?" required a call per meeting.
    ///
    /// Scoped by <see cref="Domain.Authorization.RoomReadAccess"/> over the rooms, NOT by
    /// workspace membership — a list is not permission to widen a per-room read, and the two
    /// answering differently is how a tenant-wide route becomes a leak.
    ///
    /// Only <c>IsCurrent</c> rows. A superseded version is part of one meeting's paper trail and
    /// belongs on that meeting's page; listing every version here would show the same minutes
    /// several times over, most of them wrong.
    /// </summary>
    Task<Result<WorkspaceMinutesResponse>> ListForWorkspaceAsync(
        Guid workspaceId,
        GetWorkspaceMinutesRequest request,
        Guid userId,
        string? userEmail,
        CancellationToken ct = default);

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
    Task<Result<MinutesExportFile>> ExportAsync(
        Guid roomId, Guid userId, string? userEmail, string? template, string format,
        CancellationToken ct = default);

    // ------------------------------------------------------------------ sharing

    /// <summary>
    /// The room's sharing state, creating the link on first ask.
    ///
    /// Created INVITED_ONLY: opening the dialog must not be the act that publishes a document.
    /// Host authority, because who may read a record is the host's decision, not a reader's.
    /// </summary>
    Task<Result<MinutesShareDto>> GetOrCreateShareAsync(
        Guid roomId, Guid userId, CancellationToken ct = default);

    /// <summary>Change the mode, downloads or expiry. Null fields are left as they are.</summary>
    Task<Result<MinutesShareDto>> UpdateShareAsync(
        Guid roomId, Guid userId, UpdateMinutesShareRequest request, CancellationToken ct = default);

    /// <summary>
    /// Kill the link. The token is rotated rather than flagged, so the URL in somebody's inbox is
    /// gone from the database and cannot be revived by a bug or a future "undo".
    /// </summary>
    Task<Result<MinutesShareDto>> RevokeShareAsync(
        Guid roomId, Guid userId, CancellationToken ct = default);

    Task<Result<MinutesShareDto>> AddSharePersonAsync(
        Guid roomId, Guid userId, string email, CancellationToken ct = default);

    Task<Result<MinutesShareDto>> RemoveSharePersonAsync(
        Guid roomId, Guid userId, string email, CancellationToken ct = default);

    /// <summary>
    /// The minutes behind a share token, for a viewer who may be nobody at all.
    ///
    /// <paramref name="viewerUserId"/> and <paramref name="viewerEmail"/> are null for an
    /// anonymous reader; that is a legitimate caller on a public link and a sign-in prompt on a
    /// restricted one.
    /// </summary>
    Task<Result<SharedMinutesDto>> GetSharedAsync(
        string token, Guid? viewerUserId, string? viewerEmail, CancellationToken ct = default);

    /// <summary>The same document as a file, for a viewer holding a link that allows downloads.</summary>
    Task<Result<MinutesExportFile>> ExportSharedAsync(
        string token, Guid? viewerUserId, string? viewerEmail, string? template, string format,
        CancellationToken ct = default);
}
