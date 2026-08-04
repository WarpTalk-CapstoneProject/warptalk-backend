using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using WarpTalk.TranscriptService.Infrastructure.Persistence.Contexts;

namespace WarpTalk.TranscriptService.Infrastructure.Repositories;

public class TranscriptExportRepository : GenericRepository<TranscriptExport>, ITranscriptExportRepository
{
    public TranscriptExportRepository(TranscriptDbContext context) : base(context)
    {
    }
}
