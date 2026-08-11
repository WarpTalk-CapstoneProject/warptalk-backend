using System;
using System.Threading;
using System.Threading.Tasks;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

/// <summary>
/// Hands a finished meeting summary to warptalk-ai's KnowledgeFactWorker, which indexes it
/// and extracts the durable facts the workspace Knowledge page shows.
///
/// WHY THE SUMMARY AND NOT THE TRANSCRIPT
///     Every transcript segment is already indexed one Qdrant point per sentence by the
///     transcript service. That is right for retrieval and wrong for a person: a workspace's
///     knowledge listing filled with "at 8:18 — Kỳ" rows says nothing about what the
///     workspace knows. The summary is the meeting's durable form, and until this port
///     existed nothing indexed it at all — it was written as an artifact and stopped there.
///
/// WHY IT IS FIRE-AND-FORGET
///     A meeting must finalize whether or not its summary reached the index. Implementations
///     must therefore never throw into the finalization path; a failure here costs a row on
///     a listing, while a failure there costs the meeting its artifacts.
/// </summary>
public interface IKnowledgeFactRequestPublisher
{
    /// <param name="text">
    /// The summary as written. The worker chunks and embeds it; passing the artifact JSON
    /// verbatim would index field names as if they were content.
    /// </param>
    /// <param name="indexSourceText">
    /// True when <paramref name="text"/> has not been indexed by anyone else and needs a
    /// vector of its own — which is the case for every meeting summary.
    /// </param>
    Task PublishAsync(
        Guid workspaceId,
        string sourceType,
        Guid sourceId,
        string title,
        string text,
        bool indexSourceText,
        CancellationToken ct = default);
}
