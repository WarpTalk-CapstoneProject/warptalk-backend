using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Data;

namespace WarpTalk.MeetingService.Infrastructure.Repositories;

public class PollVoteRepository : GenericRepository<PollVote>, IPollVoteRepository
{
    public PollVoteRepository(MeetingDbContext context) : base(context)
    {
    }
}
