using System;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.TranscriptService.Application.Interfaces;

/// <summary>
/// WT-605. When a room finished, or null while it is still going (and null when the answer cannot
/// be had at all).
/// </summary>
/// <remarks>
/// Its one job is repairing a pause window that was never closed — see
/// <c>TranscriptRecordingService.GetPauseWindowsAsync</c>. TranscriptService keeps no copy of a
/// room's end time and there is no foreign key to one; the fact lives in TranslationRoomService,
/// so the only way to ask is over gRPC.
///
/// Its own interface rather than another method on <see cref="Authorization.ITranscriptPauseAccess"/>:
/// that one answers "may this person do this", and hanging a display detail off an authorization
/// port is how an authorization port quietly stops being one.
///
/// Null on any failure, never an exception. The caller is rendering a divider; a room it cannot
/// reach means the window is shown exactly as it is stored, which is the behaviour that existed
/// before this repair and is never worse than it.
/// </remarks>
public interface ITranslationRoomEndTime
{
    Task<DateTime?> GetEndedAtAsync(Guid translationRoomId, CancellationToken cancellationToken = default);
}
