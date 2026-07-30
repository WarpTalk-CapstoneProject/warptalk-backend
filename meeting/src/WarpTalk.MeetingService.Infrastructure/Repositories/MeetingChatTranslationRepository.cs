using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Data;

namespace WarpTalk.MeetingService.Infrastructure.Repositories;

public class MeetingChatTranslationRepository : GenericRepository<MeetingChatTranslation>, IMeetingChatTranslationRepository
{
    public MeetingChatTranslationRepository(MeetingDbContext context) : base(context)
    {
    }
}
