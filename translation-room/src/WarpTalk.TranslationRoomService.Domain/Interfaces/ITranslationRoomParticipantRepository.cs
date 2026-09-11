using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Domain.Interfaces;

public interface ITranslationRoomParticipantRepository : IGenericRepository<TranslationRoomParticipant>
{
    Task<TranslationRoomParticipant?> GetByRoomAndUserAsync(Guid roomId, Guid userId, CancellationToken cancellationToken = default);
    Task<List<TranslationRoomParticipant>> GetByRoomIdAsync(Guid roomId, CancellationToken ct = default);

    /// <summary>
    /// WT-262: number of participants currently occupying a seat in the room, i.e. those whose
    /// status is in <see cref="Constants.TranslationRoomParticipantStatuses.SeatHolding"/>.
    /// WAITING (still in the lobby), DISCONNECTED, LEFT, KICKED, REJECTED and INVITED rows do not
    /// count — a stale row must never keep a live user out. Counted in the database rather than by
    /// materialising the roster, because this runs on every join.
    /// </summary>
    Task<int> CountSeatHoldingParticipantsAsync(Guid roomId, CancellationToken ct = default);

    /// <summary>
    /// WT-280: the same seat count as <see cref="CountSeatHoldingParticipantsAsync"/>, for a whole
    /// page of rooms in one round trip. Same single definition of occupancy
    /// (<see cref="Constants.TranslationRoomParticipantStatuses.SeatHolding"/>) — this exists only
    /// so the rooms list does not issue one count per room, or (as it did) count an
    /// unloaded navigation collection and report 0 for a room that has people in it.
    ///
    /// Rooms with nobody holding a seat are simply absent from the result; callers read it with
    /// GetValueOrDefault and get a genuine 0.
    /// </summary>
    Task<Dictionary<Guid, int>> CountSeatHoldingParticipantsByRoomsAsync(
        IReadOnlyCollection<Guid> roomIds,
        CancellationToken ct = default);

    /// <summary>
    /// The real people in each room right now: seat holders, minus an EXTERNAL_BRIDGE room's
    /// far-side stand-in (<see cref="Constants.RoomPresence"/>). This is what decides whether a
    /// live room is empty enough to end; the seat count above still decides capacity.
    ///
    /// Rooms with nobody in them are absent from the result, as with the seat count.
    /// </summary>
    Task<Dictionary<Guid, int>> CountPeopleInRoomsAsync(
        IReadOnlyCollection<Guid> roomIds,
        CancellationToken ct = default);

    /// <summary>
    /// How many distinct people have EVER been in each room, whatever their status now.
    ///
    /// A different question from the one above, and the list needs both. Occupancy answers "who
    /// is in there right now", which is what a live room should show — and which is always 0 for
    /// a meeting that is over, so a finished meeting reported "0/100" no matter how many people
    /// attended it. This answers "how many turned up", which is the only attendance figure a
    /// finished meeting has.
    ///
    /// DISTINCT by user: a participant who dropped and rejoined is one attendee, not two.
    /// </summary>
    Task<Dictionary<Guid, int>> CountEverJoinedByRoomsAsync(
        IReadOnlyCollection<Guid> roomIds,
        CancellationToken ct = default);

    /// <summary>
    /// WT-662: the same attendance figure as <see cref="CountEverJoinedByRoomsAsync"/>, for one
    /// room.
    ///
    /// The room LIST has had this since WT-280 and the room DETAIL never did, so the detail page
    /// fell back to the DTO's defaulted <c>AttendedCount = 0</c> and every finished meeting read
    /// "0 attended" — twice, because the People panel's summary line reads the same field.
    ///
    /// DISTINCT by user, and no status filter, for the reasons given above: the question is who
    /// turned up, and somebody who dropped and rejoined turned up once.
    /// </summary>
    Task<int> CountEverJoinedAsync(Guid roomId, CancellationToken ct = default);
}
