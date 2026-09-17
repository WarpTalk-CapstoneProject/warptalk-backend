using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.AssistantService.Infrastructure.Persistence;

namespace WarpTalk.AssistantService.Infrastructure.Repositories;

public class WorkspacePluginCurationRepository : GenericRepository<WorkspacePluginCuration>, IWorkspacePluginCurationRepository
{
    public WorkspacePluginCurationRepository(AssistantDbContext db) : base(db)
    {
    }
}
