using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.AssistantService.Infrastructure.Persistence;

namespace WarpTalk.AssistantService.Infrastructure.Repositories;

public class PlatformMessageRepository : GenericRepository<PlatformMessage>, IPlatformMessageRepository
{
    public PlatformMessageRepository(AssistantDbContext context) : base(context)
    {
    }
}
