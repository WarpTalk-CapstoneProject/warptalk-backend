using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.Application.DTOs;

namespace WarpTalk.TranscriptService.Application.Interfaces;

/// <summary>
/// Fills in the languages a meeting was never translated into while it was running.
///
/// The live pipeline translates only into the target that was selected at that moment, so the
/// saved transcript ends up with a different subset covered per language: a meeting that switched
/// from English to Japanese mid-way has the first stretch in English and the rest in Japanese, and
/// a meeting where translation was never started has nothing at all. Reading it back "in English"
/// therefore used to mean "in English where English happened to exist" — which is not reading it
/// in English.
///
/// This asks warptalk-ai to translate the gap after the fact. It does NOT persist anything itself:
/// the backfill worker publishes ordinary translation results and the existing
/// TranscriptRedisConsumerService writes them, so a backfilled line is stored exactly like a live
/// one (same dedup on (workspace, text_hash, language), same supersede-the-current-link rule).
/// </summary>
public interface ITranscriptTranslationBackfillService
{
    /// <summary>
    /// How much of the transcript can be read in <paramref name="targetLanguage"/> right now.
    /// Safe to poll — it is two indexed reads and a Redis GET, and it is how a client watches a
    /// running backfill make progress.
    /// </summary>
    Task<Result<TranscriptLanguageCoverageDto>> GetCoverageAsync(
        Guid transcriptId,
        Guid userId,
        string targetLanguage,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues the missing segments for translation and returns the coverage as it stands at that
    /// moment (so a caller can render progress from the first response onwards).
    ///
    /// Idempotent in the way that matters: a second call while one is already running is a no-op
    /// that returns the current coverage rather than queueing the same segments twice, and a call
    /// with nothing missing returns <c>idle</c> without touching Redis.
    /// </summary>
    Task<Result<TranscriptLanguageCoverageDto>> RequestBackfillAsync(
        Guid transcriptId,
        Guid userId,
        string targetLanguage,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-translates one corrected line into every language it already had a translation in.
    ///
    /// Correcting what somebody said leaves each of that line's translations describing a sentence
    /// nobody spoke. The old code published a message announcing exactly this and no worker read
    /// the stream, so a correction has never once propagated: the transcript showed the fix and
    /// every translation of it kept the mistake.
    ///
    /// Each request names the translation_contents row it replaces, so the result is stored as a
    /// retranslation rather than as a first translation.
    /// </summary>
    /// <returns>How many languages were queued. Zero when the line has no translations to redo.</returns>
    /// <remarks>
    /// Authorization is the caller's. This is reached only after a correction has been accepted
    /// and committed, and it deliberately does not re-ask a question that was already answered.
    /// </remarks>
    Task<int> RequestRetranslationAsync(Guid segmentId, CancellationToken cancellationToken = default);
}
