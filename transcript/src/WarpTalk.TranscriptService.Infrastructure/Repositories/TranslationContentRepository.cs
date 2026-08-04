using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using WarpTalk.TranscriptService.Infrastructure.Persistence.Contexts;

namespace WarpTalk.TranscriptService.Infrastructure.Repositories;

public class TranslationContentRepository : GenericRepository<TranslationContent>, ITranslationContentRepository
{
    public TranslationContentRepository(TranscriptDbContext context) : base(context)
    {
    }
}
