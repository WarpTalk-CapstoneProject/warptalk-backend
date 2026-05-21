using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Data;

namespace WarpTalk.MeetingService.Infrastructure.Repositories;

public class MeetingTrackRepository : GenericRepository<MeetingTrack>, IMeetingTrackRepository
{
    public MeetingTrackRepository(MeetingDbContext context) : base(context)
    {
    }
}
