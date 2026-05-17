using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranscriptService.Domain.Entities;

namespace WarpTalk.TranscriptService.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    IGenericRepository<Transcript> Transcripts { get; }
    IGenericRepository<TranscriptSegment> TranscriptSegments { get; }
    IGenericRepository<TranscriptTranslation> TranscriptTranslations { get; }
    IGenericRepository<TranscriptCorrection> TranscriptCorrections { get; }
    IGenericRepository<Glossary> Glossaries { get; }
    IGenericRepository<GlossaryTerm> GlossaryTerms { get; }
    IGenericRepository<TranscriptExport> TranscriptExports { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
