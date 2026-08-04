using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using WarpTalk.TranscriptService.Infrastructure.Persistence.Contexts;

namespace WarpTalk.TranscriptService.Infrastructure.Repositories;

public class TranscriptRepository : GenericRepository<Transcript>, ITranscriptRepository
{
    public TranscriptRepository(TranscriptDbContext context) : base(context)
    {
    }
}
