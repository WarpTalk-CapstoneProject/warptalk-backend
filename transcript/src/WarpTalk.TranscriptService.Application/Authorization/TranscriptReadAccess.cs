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
/// Scope: the effective host always; a participant of a meeting still running; and, once the
/// meeting has ENDED, a participant only while the room's <c>ArtifactAccess</c> says the record is
/// shared. It used to be a flat "host OR participant", which made the
/// transcript the one output the host's Publish control did not actually govern — the banner over
/// that control says it shares "the transcript, AI summary and recording", the download endpoint
/// asked <c>ArtifactAccessHelper</c>, and this asked nothing. A participant could therefore read
/// the whole transcript of a meeting whose record the host had deliberately kept private, while
/// being refused the very same text as a file. The web already draws the withheld state for this
/// case (<c>describeRecordSharing</c> → "The host has not shared this meeting's record yet"), so
/// this is the server catching up to what the product already tells people.
/// </para>
/// <para>
/// The HOST clause uses the EFFECTIVE host, so the gate follows a host transfer the way every
/// gate inside TranslationRoomService does.
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
    /// <summary>
    /// The one level that shares a meeting's record with the people who took part. Mirrors
    /// <c>ArtifactAccessLevels.AllParticipants</c> in TranslationRoomService, which this service
    /// cannot reference — the string is the contract between them, and it travels on
    /// <c>GetTranslationRoomResponse.artifact_access</c>.
    /// </summary>
    private const string AllParticipants = "ALL_PARTICIPANTS";

    /// <summary>
    /// <c>RoomStatus.ENDED</c> as the wire spells it — the same cross-service copy as
    /// <see cref="AllParticipants"/>. Compared case-insensitively: it is written by
    /// <c>Enum.ToString()</c> on one side and read as a bare string on the other.
    /// </summary>
    private const string EndedStatus = "ENDED";

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
            //
            // The EFFECTIVE host — `HostId` is the booker and does not move when the room is
            // handed over. Empty means an older TranslationRoomService, and falling back to the
            // booker is what keeps that case behaving exactly as it did.
            var effectiveHost = string.IsNullOrEmpty(room.EffectiveHostId) ? room.HostId : room.EffectiveHostId;
            if (Guid.TryParse(effectiveHost, out var hostId) && hostId == userId)
                return true;

            // ...once the meeting is over. The sharing policy governs a FINISHED meeting's
            // record — record-sharing.ts opens with that sentence, and SetArtifactAccess has a
            // route of its own because the act "only makes sense after the meeting has ended".
            //
            // This endpoint is also what the live panel reads to CATCH UP somebody who joined
            // late: `useTranscriptByRoom` in persistent-meeting-session.tsx, merged with the
            // SignalR segments so arriving twenty minutes in does not show an empty panel. Gating
            // that on the policy would refuse a person the first twenty minutes of a meeting they
            // are sitting in, listening to the captions of, in every room left on the HOST_ONLY
            // default — which is nearly all of them. Withholding a record from somebody currently
            // being told its contents protects nobody.
            if (!string.Equals(room.Status, EndedStatus, StringComparison.OrdinalIgnoreCase))
            {
                var live = await _roomClient.GetParticipantsByRoomIdAsync(
                    new GetParticipantsByRoomIdRequest { RoomId = translationRoomId.ToString() },
                    cancellationToken: cancellationToken);

                return live.Participants.Any(p =>
                    Guid.TryParse(p.Id, out var liveParticipantId) &&
                    liveParticipantId == userId);
            }

            // A participant reads it only while the record is shared.
            //
            // Compared against the level the room stores, and ANYTHING ELSE — an empty string from
            // an older server, a level this build does not know — is read as HOST_ONLY. That is the
            // direction ArtifactAccessHelper.ReadArtifactAccessLevel fails in for an unparseable
            // settings blob, and an authorization input must never widen access by being absent.
            if (!string.Equals(room.ArtifactAccess, AllParticipants, StringComparison.Ordinal))
                return false;

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
