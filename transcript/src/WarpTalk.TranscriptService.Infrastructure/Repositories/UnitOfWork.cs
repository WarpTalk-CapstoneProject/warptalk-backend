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
    
    private IGenericRepository<Transcript>? _transcripts;
    private IGenericRepository<TranscriptSegment>? _transcriptSegments;
    private IGenericRepository<TranscriptCorrection>? _transcriptCorrections;
    private IGenericRepository<Glossary>? _glossaries;
    private IGenericRepository<GlossaryTerm>? _glossaryTerms;
    private IGenericRepository<GlobalGlossaryTerm>? _globalGlossaryTerms;
    private IGenericRepository<GlobalGlossaryAudit>? _globalGlossaryAudits;
    private IGenericRepository<TranscriptExport>? _transcriptExports;
    private IGenericRepository<TranslationContent>? _translationContents;
    private IGenericRepository<SegmentTranslationLink>? _segmentTranslationLinks;
    private IGenericRepository<AudioDubbing>? _audioDubbings;

    public UnitOfWork(TranscriptDbContext context)
    {
        _context = context;
    }

    public IGenericRepository<Transcript> Transcripts => 
        _transcripts ??= new GenericRepository<Transcript>(_context);

    public IGenericRepository<TranscriptSegment> TranscriptSegments => 
        _transcriptSegments ??= new GenericRepository<TranscriptSegment>(_context);

    public IGenericRepository<TranscriptCorrection> TranscriptCorrections =>
        _transcriptCorrections ??= new GenericRepository<TranscriptCorrection>(_context);

    public IGenericRepository<Glossary> Glossaries => 
        _glossaries ??= new GenericRepository<Glossary>(_context);

    public IGenericRepository<GlossaryTerm> GlossaryTerms =>
        _glossaryTerms ??= new GenericRepository<GlossaryTerm>(_context);

    public IGenericRepository<GlobalGlossaryTerm> GlobalGlossaryTerms =>
        _globalGlossaryTerms ??= new GenericRepository<GlobalGlossaryTerm>(_context);

    public IGenericRepository<GlobalGlossaryAudit> GlobalGlossaryAudits =>
        _globalGlossaryAudits ??= new GenericRepository<GlobalGlossaryAudit>(_context);

    public IGenericRepository<TranscriptExport> TranscriptExports =>
        _transcriptExports ??= new GenericRepository<TranscriptExport>(_context);

    public IGenericRepository<TranslationContent> TranslationContents =>
        _translationContents ??= new GenericRepository<TranslationContent>(_context);

    public IGenericRepository<SegmentTranslationLink> SegmentTranslationLinks =>
        _segmentTranslationLinks ??= new GenericRepository<SegmentTranslationLink>(_context);

    public IGenericRepository<AudioDubbing> AudioDubbings =>
        _audioDubbings ??= new GenericRepository<AudioDubbing>(_context);

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
