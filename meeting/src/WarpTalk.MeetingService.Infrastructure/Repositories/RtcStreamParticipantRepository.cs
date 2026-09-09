using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Data;

namespace WarpTalk.MeetingService.Infrastructure.Repositories;

public class RtcStreamParticipantRepository : GenericRepository<RtcStreamParticipant>, IRtcStreamParticipantRepository
{
    public RtcStreamParticipantRepository(MeetingDbContext context) : base(context)
    {
    }
}
