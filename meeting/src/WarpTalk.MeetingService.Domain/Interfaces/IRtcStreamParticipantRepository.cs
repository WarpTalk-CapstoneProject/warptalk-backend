using WarpTalk.MeetingService.Domain.Entities;

namespace WarpTalk.MeetingService.Domain.Interfaces;

public interface IRtcStreamParticipantRepository : IGenericRepository<RtcStreamParticipant>
{
    /// <summary>
    /// The latest <c>left_at</c> of anyone in the meeting room, or null when nobody has left. One
    /// MAX() query, read-only (no tracking).
    /// </summary>
    Task<DateTime?> GetLatestDepartureAsync(Guid meetingRoomId, CancellationToken ct = default);
}
