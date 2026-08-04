using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using WarpTalk.TranscriptService.Infrastructure.Persistence.Contexts;

namespace WarpTalk.TranscriptService.Infrastructure.Repositories;

public class GlossaryTermRepository : GenericRepository<GlossaryTerm>, IGlossaryTermRepository
{
    public GlossaryTermRepository(TranscriptDbContext context) : base(context)
    {
    }
}
