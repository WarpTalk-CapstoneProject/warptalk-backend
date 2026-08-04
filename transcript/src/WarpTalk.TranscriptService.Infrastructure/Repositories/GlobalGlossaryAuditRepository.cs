using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using WarpTalk.TranscriptService.Infrastructure.Persistence.Contexts;

namespace WarpTalk.TranscriptService.Infrastructure.Repositories;

public class GlobalGlossaryAuditRepository : GenericRepository<GlobalGlossaryAudit>, IGlobalGlossaryAuditRepository
{
    public GlobalGlossaryAuditRepository(TranscriptDbContext context) : base(context)
    {
    }
}
