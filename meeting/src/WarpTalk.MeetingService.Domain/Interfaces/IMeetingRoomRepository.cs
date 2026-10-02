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

    /// <summary>
    /// Ends the meeting room as one conditional UPDATE and says whether THIS call is the one that
    /// ended it: <c>true</c> only when <c>ended_at</c> was still null and this call set it.
    ///
    /// "End for everyone" reaches meeting-service more than once per meeting — production showed
    /// two to four calls 0–1.8 s apart, spread across both replicas — and everything that must
    /// happen once per meeting (the <c>__MEETING_END__</c> summary trigger) hangs off this answer.
    /// A read-then-Update cannot give it: every concurrent call reads <c>ended_at = null</c>.
    ///
    /// A call that loses still leaves the status FINISHED (a join served from a stale room cache
    /// can rewrite it after the end), so ending twice repairs the row and never un-ends it.
    /// </summary>
    Task<bool> TryMarkFinishedAsync(Guid meetingRoomId, DateTime endedAtUtc, CancellationToken ct = default);

    /// <summary>
    /// WT-916 (B20). Takes a TRANSACTION-scoped lock on provisioning the meeting_rooms row for
    /// <paramref name="translationRoomId"/>, held until the caller's transaction commits or rolls
    /// back. Must be called inside a transaction: outside one the lock is released as soon as the
    /// statement ends and serialises nothing.
    ///
    /// The row has two creators — the normal join and the Google Meet bridge's stand-in token — and
    /// they routinely arrive together (the capturer's desktop asks for the bridge token while its
    /// web window is still joining). <c>idx_meeting_rooms_translation_room_id</c> is NOT unique, so
    /// two "no row yet → insert" paths running side by side would both insert, and the meeting
    /// would be split across two rows: recording state on one, participants on the other, and
    /// webhook lookups by provider_room_name landing on either. Whoever takes this lock second
    /// re-reads after the first has committed and finds the row instead of adding another.
    /// </summary>
    Task AcquireProvisioningLockAsync(Guid translationRoomId, CancellationToken ct = default);
}
