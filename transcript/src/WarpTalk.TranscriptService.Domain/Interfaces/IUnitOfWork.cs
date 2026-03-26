using WarpTalk.TranscriptService.Domain.Entities;

namespace WarpTalk.TranscriptService.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    IGenericRepository<Transcript> Transcripts { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
