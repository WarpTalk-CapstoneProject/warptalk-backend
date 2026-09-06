using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.AssistantService.Infrastructure.Persistence;

namespace WarpTalk.AssistantService.Infrastructure.Repositories;

public class PluginConnectionRepository : GenericRepository<PluginConnection>, IPluginConnectionRepository
{
    public PluginConnectionRepository(AssistantDbContext db) : base(db)
    {
    }
}
