using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using WarpTalk.TranscriptService.Infrastructure.Persistence.Contexts;

namespace WarpTalk.TranscriptService.Infrastructure.Repositories;

public class GlossaryRepository : GenericRepository<Glossary>, IGlossaryRepository
{
    public GlossaryRepository(TranscriptDbContext context) : base(context)
    {
    }
}
