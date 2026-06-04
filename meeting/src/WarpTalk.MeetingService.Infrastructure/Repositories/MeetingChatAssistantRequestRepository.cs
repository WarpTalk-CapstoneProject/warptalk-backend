using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Data;

namespace WarpTalk.MeetingService.Infrastructure.Repositories;

public class MeetingChatAssistantRequestRepository : GenericRepository<MeetingChatAssistantRequest>, IMeetingChatAssistantRequestRepository
{
    public MeetingChatAssistantRequestRepository(MeetingDbContext context) : base(context)
    {
    }
}
