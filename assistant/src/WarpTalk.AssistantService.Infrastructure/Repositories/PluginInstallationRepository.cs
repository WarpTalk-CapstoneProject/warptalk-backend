using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.AssistantService.Infrastructure.Persistence;

namespace WarpTalk.AssistantService.Infrastructure.Repositories;

public class PluginInstallationRepository : GenericRepository<PluginInstallation>, IPluginInstallationRepository
{
    public PluginInstallationRepository(AssistantDbContext db) : base(db)
    {
    }
}
