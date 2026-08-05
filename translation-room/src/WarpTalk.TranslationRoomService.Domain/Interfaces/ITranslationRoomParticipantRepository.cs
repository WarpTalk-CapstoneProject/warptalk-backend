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
}
