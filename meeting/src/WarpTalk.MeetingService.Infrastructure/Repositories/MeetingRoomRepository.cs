using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Data;

namespace WarpTalk.MeetingService.Infrastructure.Repositories;

public class MeetingRoomRepository : GenericRepository<MeetingRoom>, IMeetingRoomRepository
{
    public MeetingRoomRepository(MeetingDbContext context) : base(context)
    {
    }
}
