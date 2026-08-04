using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Data;

namespace WarpTalk.MeetingService.Infrastructure.Repositories;

public class BreakoutSessionRepository : GenericRepository<BreakoutSession>, IBreakoutSessionRepository
{
    public BreakoutSessionRepository(MeetingDbContext context) : base(context)
    {
    }
}
