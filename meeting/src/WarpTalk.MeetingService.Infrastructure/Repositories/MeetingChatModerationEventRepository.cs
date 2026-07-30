using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Data;

namespace WarpTalk.MeetingService.Infrastructure.Repositories;

public class MeetingChatModerationEventRepository : GenericRepository<MeetingChatModerationEvent>, IMeetingChatModerationEventRepository
{
    public MeetingChatModerationEventRepository(MeetingDbContext context) : base(context)
    {
    }
}
