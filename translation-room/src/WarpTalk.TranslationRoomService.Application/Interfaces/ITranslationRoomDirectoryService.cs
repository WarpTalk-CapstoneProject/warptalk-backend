using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;

namespace WarpTalk.TranslationRoomService.Application.Interfaces;

/// <summary>
/// Room lookups made by other services over gRPC. Separate from
/// <see cref="ITranslationRoomParticipantService"/>, whose operations all take a
/// requesting user id and enforce that user's permissions — a server-to-server
/// caller has no such user to check against.
/// </summary>
public interface ITranslationRoomDirectoryService
{
    /// <summary>
    /// WT-334: the room detail read WITHOUT a user check — the mesh's copy of what
    /// <see cref="ITranslationRoomService.GetTranslationRoomAsync"/> used to be for everyone.
    ///
    /// It lives here rather than as an optional <c>userId</c> on the user-facing interface because
    /// this interface is the codebase's existing statement of "server-to-server caller, no user to
    /// check against" — the same reason <see cref="GetParticipantsAsync"/> is here and not on
    /// <see cref="ITranslationRoomParticipantService"/>. That makes the exemption a property of the
    /// TYPE: this interface is resolved only by <c>TranslationRoomGrpcService</c> and has no HTTP
    /// surface, so a future controller cannot reach the unchecked read by passing a null. A
    /// nullable "skip the check" parameter would leave both callers on one signature and make the
    /// next unauthenticated read a one-word mistake.
    ///
    /// Reached only over the internal gRPC port. WorkspaceService is the caller of record
    /// (<c>TranslationRoomGrpcClient</c> → <c>DocumentAccessEvaluator</c>,
    /// <c>WorkspaceDocumentService</c>), and it applies its own document-level authorization to the
    /// answer.
    /// </summary>
    Task<Result<TranslationRoomDto>> GetRoomAsync(
        Guid translationRoomId,
        CancellationToken ct = default);

    Task<Result<IReadOnlyList<TranslationRoomParticipantSummaryDto>>> GetParticipantsAsync(
        Guid translationRoomId,
        CancellationToken ct = default);

    Task<Result<int>> CountActiveRoomsByWorkspaceAsync(
        Guid workspaceId,
        CancellationToken ct = default);

    /// <summary>
    /// WT-359: record a host handover that MeetingService has already authorized.
    ///
    /// This is the only WRITE on an interface otherwise made of lookups. Unlike the reads it DOES
    /// authorize, because host authority is this service's own data: the caller must be the
    /// effective host (<see cref="Domain.Entities.TranslationRoom.IsHostedBy"/>). The booker is
    /// refused once they have handed the room over — WT-359 requires that they get it back only if
    /// the incoming host transfers it back.
    ///
    /// What it writes, in one transaction:
    ///   - <c>translation_rooms.active_host_id</c> — host authority for every subsequent join and
    ///     every host-gated operation in this service.
    ///   - the incoming host's participant row to HOST, the outgoing host's to PARTICIPANT — so
    ///     the People panel is correct on its next read even if the realtime event is missed.
    ///
    /// Returns the host it replaced, so the caller can announce both sides without a second read.
    /// Idempotent: transferring to the user who already holds the room succeeds and changes nothing.
    /// </summary>
    /// <summary>
    /// WT-564: write the TERMINAL kicked status onto this service's roster.
    ///
    /// MeetingService owns and authorizes the kick, but KICKED lives here — it is what
    /// JoinTranslationRoomAsync refuses on (BR-010). Host authority is re-checked against THIS
    /// service's tables rather than trusted from the caller, the same way TransferHostAsync does.
    ///
    /// Returns false when the person had no roster row, which is not a failure: there was nothing
    /// to terminate and the kick still stands.
    /// </summary>
    Task<Result<bool>> KickParticipantByUserAsync(
        Guid translationRoomId,
        Guid requestedByUserId,
        Guid participantUserId,
        CancellationToken ct = default);

    Task<Result<Guid>> TransferHostAsync(
        Guid translationRoomId,
        Guid requestedByUserId,
        Guid newHostUserId,
        CancellationToken ct = default);
}
