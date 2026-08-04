using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Data;

namespace WarpTalk.MeetingService.Infrastructure.Repositories;

public class MeetingInvitationRepository : GenericRepository<MeetingInvitation>, IMeetingInvitationRepository
{
    public MeetingInvitationRepository(MeetingDbContext context) : base(context)
    {
    }
}
