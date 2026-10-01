using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranscriptService.Domain.Entities;

namespace WarpTalk.TranscriptService.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    ITranscriptRepository Transcripts { get; }
    ITranscriptSegmentRepository TranscriptSegments { get; }
    ITranscriptCorrectionRepository TranscriptCorrections { get; }
    IGlossaryRepository Glossaries { get; }
    IGlossaryTermRepository GlossaryTerms { get; }
    IGlobalGlossaryTermRepository GlobalGlossaryTerms { get; }
    IGlobalGlossaryAuditRepository GlobalGlossaryAudits { get; }
    ITranscriptExportRepository TranscriptExports { get; }
    ITranslationContentRepository TranslationContents { get; }
    ISegmentTranslationLinkRepository SegmentTranslationLinks { get; }
    IAudioDubbingRepository AudioDubbings { get; }
    ITranscriptPauseWindowRepository TranscriptPauseWindows { get; }
    ITranscriptCleanSentenceRepository TranscriptCleanSentences { get; }

    /// <summary>
    /// Atomically advances a transcript for one new segment: increments last_sequence_order,
    /// mirrors it into total_segments, and folds endMs into total_duration_ms — all in a single
    /// "UPDATE ... RETURNING" statement — then returns the new sequence order.
    ///
    /// Do NOT replace with a read-then-increment-then-save pattern in C# — that races under
    /// concurrent writers (see migration 017-15-07-2026-translation-cluster-finalize.sql STEP 1).
    ///
    /// Just as importantly: do NOT follow this call with `_unitOfWork.Transcripts.Update(transcript)`
    /// on the entity that was read earlier in the same method. EF Core's Update() marks EVERY
    /// property as modified using whatever is in the tracked entity's in-memory snapshot — since
    /// that snapshot was never refreshed with the value this method just wrote, a trailing
    /// Update()+SaveChanges silently reverts last_sequence_order/total_segments/total_duration_ms
    /// back to their stale pre-call values, corrupting the counter for the NEXT segment (this was
    /// found live: two segments could not simultaneously hold the correct total_segments count).
    /// That's why this method also owns total_segments/total_duration_ms — so nothing else needs
    /// to touch those three columns via the change tracker at all.
    /// </summary>
    Task<int> AdvanceTranscriptForNewSegmentAsync(Guid transcriptId, int endTimeMs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stamps <c>transcripts.timeline_anchor_at</c> if, and only if, it is still NULL — a single
    /// "UPDATE ... WHERE timeline_anchor_at IS NULL" that touches no other column. Returns whether
    /// this call was the one that set it.
    ///
    /// It exists for the same reason as <see cref="AdvanceTranscriptForNewSegmentAsync"/>, and
    /// because the warning above was not followed: the anchor used to be written with
    /// <c>Transcripts.Update(transcript)</c> on the entity read before the counter advanced. That
    /// Update() marked EVERY column modified, so the first segment of every meeting wrote
    /// last_sequence_order back to its stale value, the meeting's SECOND segment was handed a
    /// sequence_order that already existed, failed the unique index, sat pending until the stale
    /// reclaim ~70 s later, and was then stored with the LAST sequence_order of the meeting while
    /// keeping its real start time — the "line at 0:05 shown at the bottom" transcript order bug
    /// (prod room 01a0f630, 1 Oct 2026).
    ///
    /// The IS NULL guard also makes first-write-wins hold across replicas, which the in-memory
    /// check on a tracked entity could not.
    /// </summary>
    Task<bool> StampTranscriptTimelineAnchorAsync(Guid transcriptId, DateTime anchorUtc, CancellationToken cancellationToken = default);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
