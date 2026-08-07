using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using GetParticipantsByRoomIdRequest = WarpTalk.Shared.Protos.GetParticipantsByRoomIdRequest;
using GetTranslationRoomRequest = WarpTalk.Shared.Protos.GetTranslationRoomRequest;
using TranslationRoomServiceClient = WarpTalk.Shared.Protos.TranslationRoomService.TranslationRoomServiceClient;

namespace WarpTalk.TranscriptService.Application.Authorization;

/// <summary>
/// The single definition of "who may read this transcript".
///
/// This predicate had been written out by hand three times — <c>TranscriptQueryService</c>,
/// <c>TranscriptCorrectionService</c> and <c>TranscriptExportService</c> each carried a private
/// <c>CanAccessTranscriptAsync</c> that was byte-identical to the others. Then one of them drifted:
/// the query copy had its participant clause commented out and replaced with a bare
/// <c>return true</c>, so reading a whole transcript was ungated while correcting a single line of
/// it stayed gated. Three copies of an authorization decision is the bug that let that happen
/// silently, and it is the same failure mode <c>RoomReadAccess</c> (WT-304, translation-room
/// Domain) was introduced for on the other side of the system. The clause now lives here and the
/// call sites consume it instead of restating it.
/// </summary>
/// <remarks>
/// <para>
/// Scope: host OR participant. That is exactly what the three copies agreed on before the drift.
/// </para>
/// <para>
/// It deliberately does NOT admit an invited-by-email user who never joined, even though
/// <c>RoomReadAccess</c> does grant such a user room-level read on the translation-room side. Two
/// reasons. First, capability: the transcript service reaches rooms only over
/// <c>translation_room.proto</c>, which exposes the room and its participants and has no
/// invitation-aware RPC at all, so honouring invitations here would mean a cross-service contract
/// change. Second, intent: a standing invitation is what puts a room on your list and lets you
/// through the door; it is not consent to read what was said inside a meeting you never attended.
/// Widening this to invitees should be a product decision with its own ticket, not a side effect of
/// restoring a check.
/// </para>
/// </remarks>
public interface ITranscriptReadAccess
{
    /// <summary>
    /// True when <paramref name="userId"/> is the room's host or one of its participants.
    /// A room that no longer exists returns false rather than throwing, so callers surface the
    /// transcript as inaccessible instead of a 500.
    /// </summary>
    Task<bool> CanReadRoomTranscriptAsync(Guid translationRoomId, Guid userId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ITranscriptReadAccess"/>
public sealed class TranscriptReadAccess : ITranscriptReadAccess
{
    private readonly TranslationRoomServiceClient _roomClient;

    public TranscriptReadAccess(TranslationRoomServiceClient roomClient)
    {
        _roomClient = roomClient;
    }

    public async Task<bool> CanReadRoomTranscriptAsync(
        Guid translationRoomId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var room = await _roomClient.GetTranslationRoomByIdAsync(
                new GetTranslationRoomRequest { Id = translationRoomId.ToString() },
                cancellationToken: cancellationToken);

            // Host first: it is the common case for the pages that read a transcript, and it
            // answers without the second round trip below.
            if (Guid.TryParse(room.HostId, out var hostId) && hostId == userId)
                return true;

            var participants = await _roomClient.GetParticipantsByRoomIdAsync(
                new GetParticipantsByRoomIdRequest { RoomId = translationRoomId.ToString() },
                cancellationToken: cancellationToken);

            // Participation is enough whatever the participant's current Status: someone who has
            // since LEFT an ended meeting was still in the room while it was recorded, and the
            // transcript pages are read after the fact by definition.
            return participants.Participants.Any(p =>
                Guid.TryParse(p.Id, out var participantUserId) &&
                participantUserId == userId);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return false;
        }
    }
}
