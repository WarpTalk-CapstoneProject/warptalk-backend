using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Data;

namespace WarpTalk.MeetingService.Infrastructure.Repositories;

public class QuestionVoteRepository : GenericRepository<QuestionVote>, IQuestionVoteRepository
{
    public QuestionVoteRepository(MeetingDbContext context) : base(context)
    {
    }
}
