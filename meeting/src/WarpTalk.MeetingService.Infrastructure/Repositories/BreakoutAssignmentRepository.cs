using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Data;

namespace WarpTalk.MeetingService.Infrastructure.Repositories;

public class BreakoutAssignmentRepository : GenericRepository<BreakoutAssignment>, IBreakoutAssignmentRepository
{
    public BreakoutAssignmentRepository(MeetingDbContext context) : base(context)
    {
    }
}
