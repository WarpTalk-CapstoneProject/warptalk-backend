using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.AssistantService.Infrastructure.Persistence;

namespace WarpTalk.AssistantService.Infrastructure.Repositories;

public class AssistantToolCallRepository : GenericRepository<AssistantToolCall>, IAssistantToolCallRepository
{
    public AssistantToolCallRepository(AssistantDbContext context) : base(context)
    {
    }
}
