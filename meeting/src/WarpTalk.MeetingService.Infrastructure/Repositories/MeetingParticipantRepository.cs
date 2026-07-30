using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Data;

namespace WarpTalk.MeetingService.Infrastructure.Repositories;

public class MeetingParticipantRepository : GenericRepository<MeetingParticipant>, IMeetingParticipantRepository
{
    public MeetingParticipantRepository(MeetingDbContext context) : base(context)
    {
    }
}
