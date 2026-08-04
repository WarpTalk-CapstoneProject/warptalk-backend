using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using WarpTalk.TranscriptService.Infrastructure.Persistence.Contexts;

namespace WarpTalk.TranscriptService.Infrastructure.Repositories;

public class SegmentTranslationLinkRepository : GenericRepository<SegmentTranslationLink>, ISegmentTranslationLinkRepository
{
    public SegmentTranslationLinkRepository(TranscriptDbContext context) : base(context)
    {
    }
}
