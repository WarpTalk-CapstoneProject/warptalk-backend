using WarpTalk.MeetingService.Domain.Entities;

namespace WarpTalk.MeetingService.Domain.Interfaces;

public interface IMeetingRoomRepository : IGenericRepository<MeetingRoom>
{
    /// <summary>
    /// Clears the room's active host ONLY if it is still <paramref name="expectedHostId"/>, as one
    /// conditional UPDATE, and returns the number of rows changed (0 or 1).
    ///
    /// Every meeting-service replica handles the same participant-offline pub/sub message, and a
    /// read-then-Update of the whole row could erase a host claimed in between, or write stale
    /// values over other columns. The compare-and-set makes the handler safe to run N times.
    /// </summary>
    Task<int> ClearActiveHostIfAsync(Guid translationRoomId, Guid expectedHostId, CancellationToken ct = default);
}
