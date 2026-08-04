using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using WarpTalk.TranscriptService.Infrastructure.Persistence.Contexts;

namespace WarpTalk.TranscriptService.Infrastructure.Repositories;

public class GlobalGlossaryTermRepository : GenericRepository<GlobalGlossaryTerm>, IGlobalGlossaryTermRepository
{
    public GlobalGlossaryTermRepository(TranscriptDbContext context) : base(context)
    {
    }
}
