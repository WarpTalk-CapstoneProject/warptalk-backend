using System;
using System.Threading.Tasks;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Interfaces;

/// <summary>
/// WT-699: what a kick or reject did on the room service's roster. All three are successes.
/// </summary>
public enum RoomRosterRemoval
{
    /// <summary>No roster row for that person — nothing to terminate.</summary>
    NotOnRoster,

    /// <summary>This call wrote the terminal status.</summary>
    Removed,

    /// <summary>The terminal status was already there; nothing was written.</summary>
    AlreadyRemoved,
}

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
    /// <remarks>
    /// WT-699 / TC2103: answers which of three things happened, so a second press can be reported
    /// as "already removed" instead of as a fresh kick.
    /// </remarks>
    Task<Result<RoomRosterRemoval>> KickRoomParticipantAsync(
        Guid translationRoomId,
        Guid requestedByUserId,
        Guid participantUserId);

    /// <summary>
    /// WT-699 / TC2402: carry a lobby Reject to the room service, where the waiting-room row lives.
    /// Without it the knock stayed WAITING — still listed, still admittable.
    /// </summary>
    Task<Result<RoomRosterRemoval>> RejectRoomParticipantAsync(
        Guid translationRoomId,
        Guid requestedByUserId,
        Guid participantUserId);

    Task<Result<Guid>> TransferRoomHostAsync(Guid translationRoomId, Guid requestedByUserId, Guid newHostUserId);
}
