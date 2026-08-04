using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using WarpTalk.TranscriptService.Infrastructure.Persistence.Contexts;

namespace WarpTalk.TranscriptService.Infrastructure.Repositories;

public class TranscriptCorrectionRepository : GenericRepository<TranscriptCorrection>, ITranscriptCorrectionRepository
{
    public TranscriptCorrectionRepository(TranscriptDbContext context) : base(context)
    {
    }
}
