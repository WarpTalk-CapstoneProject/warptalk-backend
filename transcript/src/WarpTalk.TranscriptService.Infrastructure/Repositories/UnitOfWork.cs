using System;
using System.Threading;
using System.Threading.Tasks;
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
    private IGenericRepository<TranscriptTranslation>? _transcriptTranslations;
    private IGenericRepository<TranscriptCorrection>? _transcriptCorrections;
    private IGenericRepository<Glossary>? _glossaries;
    private IGenericRepository<GlossaryTerm>? _glossaryTerms;
    private IGenericRepository<TranscriptExport>? _transcriptExports;

    public UnitOfWork(TranscriptDbContext context)
    {
        _context = context;
    }

    public IGenericRepository<Transcript> Transcripts => 
        _transcripts ??= new GenericRepository<Transcript>(_context);

    public IGenericRepository<TranscriptSegment> TranscriptSegments => 
        _transcriptSegments ??= new GenericRepository<TranscriptSegment>(_context);

    public IGenericRepository<TranscriptTranslation> TranscriptTranslations => 
        _transcriptTranslations ??= new GenericRepository<TranscriptTranslation>(_context);

    public IGenericRepository<TranscriptCorrection> TranscriptCorrections => 
        _transcriptCorrections ??= new GenericRepository<TranscriptCorrection>(_context);

    public IGenericRepository<Glossary> Glossaries => 
        _glossaries ??= new GenericRepository<Glossary>(_context);

    public IGenericRepository<GlossaryTerm> GlossaryTerms => 
        _glossaryTerms ??= new GenericRepository<GlossaryTerm>(_context);

    public IGenericRepository<TranscriptExport> TranscriptExports => 
        _transcriptExports ??= new GenericRepository<TranscriptExport>(_context);

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return await _context.SaveChangesAsync(cancellationToken);
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}
