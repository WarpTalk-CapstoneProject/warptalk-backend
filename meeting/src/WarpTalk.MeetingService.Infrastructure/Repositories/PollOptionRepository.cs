using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Data;

namespace WarpTalk.MeetingService.Infrastructure.Repositories;

public class PollOptionRepository : GenericRepository<PollOption>, IPollOptionRepository
{
    public PollOptionRepository(MeetingDbContext context) : base(context)
    {
    }
}
