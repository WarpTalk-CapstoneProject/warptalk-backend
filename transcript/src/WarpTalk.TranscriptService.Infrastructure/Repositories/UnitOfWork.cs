using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using WarpTalk.TranscriptService.Infrastructure.Persistence;
using WarpTalk.TranscriptService.Infrastructure.Persistence.Contexts;

namespace WarpTalk.TranscriptService.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly TranscriptDbContext _context;

    private ITranscriptRepository? _transcripts;
    private ITranscriptSegmentRepository? _transcriptSegments;
    private ITranscriptCorrectionRepository? _transcriptCorrections;
    private IGlossaryRepository? _glossaries;
    private IGlossaryTermRepository? _glossaryTerms;
    private IGlobalGlossaryTermRepository? _globalGlossaryTerms;
    private IGlobalGlossaryAuditRepository? _globalGlossaryAudits;
    private ITranscriptExportRepository? _transcriptExports;
    private ITranslationContentRepository? _translationContents;
    private ISegmentTranslationLinkRepository? _segmentTranslationLinks;
    private IAudioDubbingRepository? _audioDubbings;
    private ITranscriptPauseWindowRepository? _transcriptPauseWindows;
    private ITranscriptCleanSentenceRepository? _transcriptCleanSentences;

    public UnitOfWork(TranscriptDbContext context)
    {
        _context = context;
    }

    public ITranscriptRepository Transcripts =>
        _transcripts ??= new TranscriptRepository(_context);

    public ITranscriptSegmentRepository TranscriptSegments =>
        _transcriptSegments ??= new TranscriptSegmentRepository(_context);

    public ITranscriptCorrectionRepository TranscriptCorrections =>
        _transcriptCorrections ??= new TranscriptCorrectionRepository(_context);

    public IGlossaryRepository Glossaries =>
        _glossaries ??= new GlossaryRepository(_context);

    public IGlossaryTermRepository GlossaryTerms =>
        _glossaryTerms ??= new GlossaryTermRepository(_context);

    public IGlobalGlossaryTermRepository GlobalGlossaryTerms =>
        _globalGlossaryTerms ??= new GlobalGlossaryTermRepository(_context);

    public IGlobalGlossaryAuditRepository GlobalGlossaryAudits =>
        _globalGlossaryAudits ??= new GlobalGlossaryAuditRepository(_context);

    public ITranscriptExportRepository TranscriptExports =>
        _transcriptExports ??= new TranscriptExportRepository(_context);

    public ITranslationContentRepository TranslationContents =>
        _translationContents ??= new TranslationContentRepository(_context);

    public ISegmentTranslationLinkRepository SegmentTranslationLinks =>
        _segmentTranslationLinks ??= new SegmentTranslationLinkRepository(_context);

    public IAudioDubbingRepository AudioDubbings =>
        _audioDubbings ??= new AudioDubbingRepository(_context);

    public ITranscriptPauseWindowRepository TranscriptPauseWindows =>
        _transcriptPauseWindows ??= new TranscriptPauseWindowRepository(_context);

    public ITranscriptCleanSentenceRepository TranscriptCleanSentences =>
        _transcriptCleanSentences ??= new TranscriptCleanSentenceRepository(_context);

    public async Task<int> AdvanceTranscriptForNewSegmentAsync(Guid transcriptId, int endTimeMs, CancellationToken cancellationToken = default)
    {
        var result = await _context.Database
            .SqlQueryRaw<int>(
                """
                UPDATE transcript.transcripts
                SET last_sequence_order = last_sequence_order + 1,
                    total_segments = last_sequence_order + 1,
                    total_duration_ms = GREATEST(total_duration_ms, {1}),
                    updated_at = now()
                WHERE id = {0}
                RETURNING last_sequence_order
                """,
                transcriptId, endTimeMs)
            .ToListAsync(cancellationToken);
        return result.Single();
    }

    public async Task<bool> StampTranscriptTimelineAnchorAsync(Guid transcriptId, DateTime anchorUtc, CancellationToken cancellationToken = default)
    {
        // Only this column, and only while it is unset — see the interface doc comment.
        var rows = await _context.Database.ExecuteSqlRawAsync(
            """
            UPDATE transcript.transcripts
            SET timeline_anchor_at = {1}
            WHERE id = {0} AND timeline_anchor_at IS NULL
            """,
            new object[] { transcriptId, anchorUtc },
            cancellationToken);
        return rows > 0;
    }

    public async Task<int?> PlaceSegmentByStartTimeAsync(Guid transcriptId, Guid segmentId, CancellationToken cancellationToken = default)
    {
        // See the interface doc comment. Every statement below runs inside this one transaction,
        // after the transcript row lock, so two replicas never compute a position from each
        // other's half-finished shift.
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        await _context.Database.ExecuteSqlRawAsync(
            "SELECT 1 FROM transcript.transcripts WHERE id = {0} FOR UPDATE",
            new object[] { transcriptId },
            cancellationToken);

        var current = (await _context.Database
            .SqlQueryRaw<int>(
                """
                SELECT sequence_order AS "Value"
                FROM transcript.transcript_segments
                WHERE id = {0} AND transcript_id = {1}
                """,
                segmentId, transcriptId)
            .ToListAsync(cancellationToken)).SingleOrDefault();

        // The first segment stored BEFORE this one that started AFTER it: that is the slot this one
        // belongs in. 0 (none) means the segment is already in place, which is the normal case.
        // COALESCE to a plain int rather than reading a nullable scalar: one row always comes back.
        var target = (await _context.Database
            .SqlQueryRaw<int>(
                """
                SELECT COALESCE(MIN(o.sequence_order), 0) AS "Value"
                FROM transcript.transcript_segments s
                JOIN transcript.transcript_segments o ON o.transcript_id = s.transcript_id
                WHERE s.id = {0}
                  AND s.transcript_id = {1}
                  AND o.sequence_order < s.sequence_order
                  AND o.start_time_ms > s.start_time_ms
                """,
                segmentId, transcriptId)
            .ToListAsync(cancellationToken)).SingleOrDefault();

        if (current <= 0 || target <= 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        // [target, current] -> negative and distinct (the segment itself becomes -(current+1)),
        // then [target, current-1] back up by one, then the segment into the freed `target`. Each
        // step leaves every positive sequence_order unique, which a non-deferrable index requires.
        await _context.Database.ExecuteSqlRawAsync(
            """
            UPDATE transcript.transcript_segments
            SET sequence_order = -(sequence_order + 1)
            WHERE transcript_id = {0} AND sequence_order >= {1} AND sequence_order <= {2}
            """,
            new object[] { transcriptId, target, current },
            cancellationToken);
        await _context.Database.ExecuteSqlRawAsync(
            """
            UPDATE transcript.transcript_segments
            SET sequence_order = -sequence_order
            WHERE transcript_id = {0} AND sequence_order < 0 AND id <> {1}
            """,
            new object[] { transcriptId, segmentId },
            cancellationToken);
        await _context.Database.ExecuteSqlRawAsync(
            """
            UPDATE transcript.transcript_segments
            SET sequence_order = {1}
            WHERE id = {0}
            """,
            new object[] { segmentId, target },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return target;
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return await _context.SaveChangesAsync(cancellationToken);
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}
