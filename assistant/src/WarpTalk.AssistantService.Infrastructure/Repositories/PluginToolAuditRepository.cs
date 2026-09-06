using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.AssistantService.Infrastructure.Persistence;

namespace WarpTalk.AssistantService.Infrastructure.Repositories;

public class PluginToolAuditRepository : GenericRepository<PluginToolAudit>, IPluginToolAuditRepository
{
    public PluginToolAuditRepository(AssistantDbContext db) : base(db)
    {
    }
}
