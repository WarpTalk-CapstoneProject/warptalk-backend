using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using WarpTalk.TranscriptService.Infrastructure.Persistence;

namespace WarpTalk.TranscriptService.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly TranscriptDbContext _context;
    
    public IGenericRepository<Transcript> Transcripts { get; }

    public UnitOfWork(TranscriptDbContext context)
    {
        _context = context;
        Transcripts = new GenericRepository<Transcript>(_context);
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        return await _context.SaveChangesAsync(ct);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}
