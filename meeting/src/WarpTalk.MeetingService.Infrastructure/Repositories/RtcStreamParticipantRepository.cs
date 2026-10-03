using Microsoft.EntityFrameworkCore;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Data;

namespace WarpTalk.MeetingService.Infrastructure.Repositories;

public class RtcStreamParticipantRepository : GenericRepository<RtcStreamParticipant>, IRtcStreamParticipantRepository
{
    public RtcStreamParticipantRepository(MeetingDbContext context) : base(context)
    {
    }

    public Task<DateTime?> GetLatestDepartureAsync(Guid meetingRoomId, CancellationToken ct = default) =>
        _dbSet
            .AsNoTracking()
            .Where(p => p.MeetingRoomId == meetingRoomId && p.LeftAt != null)
            .MaxAsync(p => p.LeftAt, ct);
}
