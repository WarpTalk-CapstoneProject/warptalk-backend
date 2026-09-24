using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.AssistantService.Infrastructure.Persistence;

namespace WarpTalk.AssistantService.Infrastructure.Repositories;

public class PlatformConversationRepository : GenericRepository<PlatformConversation>, IPlatformConversationRepository
{
    public PlatformConversationRepository(AssistantDbContext context) : base(context)
    {
    }
}
