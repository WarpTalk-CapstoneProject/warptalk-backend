using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Data;

namespace WarpTalk.MeetingService.Infrastructure.Repositories;

public class MeetingChatMessageRepository : GenericRepository<MeetingChatMessage>, IMeetingChatMessageRepository
{
    public MeetingChatMessageRepository(MeetingDbContext context) : base(context)
    {
    }
}
