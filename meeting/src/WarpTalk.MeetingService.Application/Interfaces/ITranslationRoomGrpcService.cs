using System;
using System.Threading.Tasks;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Interfaces;

public interface ITranslationRoomGrpcService
{
    Task<Result<Shared.Protos.GetTranslationRoomResponse>> GetRoomDetailsAsync(Guid translationRoomId);
    Task<Result<Shared.Protos.GetParticipantsByRoomIdResponse>> GetParticipantsAsync(Guid translationRoomId);

    /// <summary>
    /// WT-359: tell the translation-room service the host moved. Returns the host it replaced.
    ///
    /// Host authority lives in that service's tables — every join and every host-gated operation
    /// reads it there — so a transfer that only updates <c>meeting_rooms.active_host_id</c> is not
    /// a transfer at all: the old host is handed the room back the next time they rejoin.
    /// </summary>
    /// <remarks>
    /// That service authorizes the transfer itself against its own host column, so this is not a
    /// blind write — a caller who is no longer the host is refused there, which is what stops the
    /// outgoing host taking the room back on their own.
    /// </remarks>
    /// <summary>
    /// WT-564: carry the kick through to the room service, where the TERMINAL status lives.
    /// Without it a kicked participant is only disconnected, and the rejoin path there reads a
    /// disconnected roster row as proof they were already admitted.
    /// </summary>
    Task<Result<bool>> KickRoomParticipantAsync(
        Guid translationRoomId,
        Guid requestedByUserId,
        Guid participantUserId);

    Task<Result<Guid>> TransferRoomHostAsync(Guid translationRoomId, Guid requestedByUserId, Guid newHostUserId);
}
