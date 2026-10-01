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
/// WT-849: an invited-but-absent user IS now admitted, but only once the meeting has ENDED and
/// only when the room's ArtifactAccess has been opened to ALL_PARTICIPANTS — never while the
/// meeting is still running. <c>translation_room.proto</c> grew <c>requester_email</c> /
/// <c>is_requester_invited</c> for exactly this (<c>GetTranslationRoomResponse</c>), computed by
/// TranslationRoomService against the same <c>TranslationRoomInvitations</c> table
/// <c>ArtifactAccessHelper.IsParticipantOrInvited</c> asks on that side, so the two services agree
/// on who counts as "invited" without this service touching that table itself.
///
/// The live-meeting branch below still asks only the roster: a standing invitation is what puts a
/// room on your list, not consent to read a meeting you never attended, and that reasoning stands
/// unchanged while the meeting has not yet been shared with anyone.
/// </para>
/// </remarks>
public interface ITranscriptReadAccess
{
    /// <summary>
    /// True when <paramref name="userId"/> is the room's host or one of its participants.
    /// </summary>
    Task<bool> CanReadRoomTranscriptAsync(
        Guid translationRoomId,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// True when <paramref name="userId"/> is the room's host, one of its participants, or — once
    /// the meeting has ENDED and its ArtifactAccess is ALL_PARTICIPANTS — an invitee named at
    /// <paramref name="userEmail"/> who never joined.
    /// A room that no longer exists returns false rather than throwing, so callers surface the
    /// transcript as inaccessible instead of a 500.
    /// </summary>
    Task<bool> CanReadRoomTranscriptAsync(
        Guid translationRoomId,
        Guid userId,
        string? userEmail,
        CancellationToken cancellationToken = default);
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

    public Task<bool> CanReadRoomTranscriptAsync(
        Guid translationRoomId,
        Guid userId,
        CancellationToken cancellationToken = default)
        => CanReadRoomTranscriptAsync(translationRoomId, userId, null, cancellationToken);

    public async Task<bool> CanReadRoomTranscriptAsync(
        Guid translationRoomId,
        Guid userId,
        string? userEmail,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new GetTranslationRoomRequest { Id = translationRoomId.ToString() };
            // WT-849: only worth asking for when we have an email to ask about — an empty string
            // reaches the server as "no requester_email", which is exactly what an absent claim
            // should mean.
            if (!string.IsNullOrWhiteSpace(userEmail))
                request.RequesterEmail = userEmail;

            var room = await _roomClient.GetTranslationRoomByIdAsync(
                request,
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
            if (participants.Participants.Any(p =>
                Guid.TryParse(p.Id, out var participantUserId) &&
                participantUserId == userId))
            {
                return true;
            }

            // WT-849: an invited-but-absent user is admitted here too, but only once we have
            // reached this point — the meeting is ENDED and ArtifactAccess is ALL_PARTICIPANTS.
            // IsRequesterInvited has field presence (HasIsRequesterInvited): unset means either no
            // userEmail was sent above, or this server predates the field, and either way the
            // answer must be false rather than an assumed true — an authorization input is never
            // allowed to widen access by being absent.
            return room.HasIsRequesterInvited && room.IsRequesterInvited;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return false;
        }
    }
}
