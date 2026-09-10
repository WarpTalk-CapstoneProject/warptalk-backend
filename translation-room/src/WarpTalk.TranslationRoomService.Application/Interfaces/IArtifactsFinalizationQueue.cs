using System;
using System.Collections.Generic;
using System.Threading;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

/// <summary>
/// A room queued for finalization, and — when a person asked for it — the summary they asked for.
///
/// WHY THE SHAPE TRAVELS WITH THE ROOM.
///     Finalization is normally a background act: a meeting ended, nobody is waiting, and the
///     summary lands in the default shape because no one has expressed an opinion yet.
///
///     There is one caller for which that is false. RegenerateSummaryAsync redirects to
///     finalization when a meeting has no artifacts at all — the host pressed "Standup" on a
///     meeting showing no summary, which the code there notes is the meeting somebody is MOST
///     likely to press it on. Their choice used to stop at that redirect: the request returned
///     success, a General summary arrived, and the picker snapped back to General. From the
///     host's side the control simply did not work.
///
///     Null means nobody asked, which is every automatic path and is why they need no change.
/// </summary>
/// <param name="RoomId">The room to finalize.</param>
/// <param name="TemplateKey">The shape a person asked for, or null for the default.</param>
/// <param name="SummaryLanguage">The language a person asked for, or null for the room's own.</param>
public readonly record struct FinalizationRequest(
    Guid RoomId,
    string? TemplateKey = null,
    string? SummaryLanguage = null);

public interface IArtifactsFinalizationQueue
{
    /// <summary>
    /// Queue a room. <paramref name="templateKey"/> and <paramref name="summaryLanguage"/> are
    /// supplied only by the path where a person expressed a preference — see
    /// <see cref="FinalizationRequest"/>.
    /// </summary>
    void QueueFinalization(Guid roomId, string? templateKey = null, string? summaryLanguage = null);

    IAsyncEnumerable<FinalizationRequest> ReadAllAsync(CancellationToken ct);
}
