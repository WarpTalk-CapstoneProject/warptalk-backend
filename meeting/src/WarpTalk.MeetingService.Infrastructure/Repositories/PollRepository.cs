using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Data;

namespace WarpTalk.MeetingService.Infrastructure.Repositories;

public class PollRepository : GenericRepository<Poll>, IPollRepository
{
    public PollRepository(MeetingDbContext context) : base(context)
    {
    }
}
