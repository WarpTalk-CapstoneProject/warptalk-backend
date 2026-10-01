using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.AssistantService.Infrastructure.Persistence;

namespace WarpTalk.AssistantService.Infrastructure.Repositories;

public class WorkspacePluginRepository : GenericRepository<WorkspacePlugin>, IWorkspacePluginRepository
{
    public WorkspacePluginRepository(AssistantDbContext db) : base(db)
    {
    }
}
