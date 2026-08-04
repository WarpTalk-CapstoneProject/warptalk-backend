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

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return await _context.SaveChangesAsync(cancellationToken);
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}
