using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Authorization;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Domain.Authorization;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Services;

public class TranslationRoomParticipantService : ITranslationRoomParticipantService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITranslationRoomRepository _translationRoomRepository;
    private readonly ITranslationRoomParticipantRepository _participantRepository;
    private readonly IWorkspaceMemberDirectory _workspaceMemberDirectory;
    private readonly IRedisStateRepository? _redisStateRepository;
    private readonly ILogger<TranslationRoomParticipantService> _logger;

    /// <summary>
    /// The same cross-process relay <see cref="TranslationRoomService"/> publishes RoomStarted and
    /// RoomEnded on. The SignalR hub lives in the Gateway process, so this service cannot reach
    /// connected clients directly — TranslationRoomRedisSubscriberService fans out to the room's
    /// group.
    /// </summary>
    private const string GatewayCommandsChannel = "warptalk:translation-room:commands";

    /// <summary>
    /// The relay command TranslationRoomRedisSubscriberService turns into the "ParticipantAdmitted"
    /// SignalR event. Named to sit beside the RoomStarted/RoomEnded commands the same channel
    /// already carries.
    /// </summary>
    private const string ParticipantAdmittedCommand = "ParticipantAdmitted";

    public TranslationRoomParticipantService(
        IUnitOfWork unitOfWork,
        IWorkspaceMemberDirectory workspaceMemberDirectory,
        ILogger<TranslationRoomParticipantService> logger,
        IRedisStateRepository? redisStateRepository = null)
    {
        _unitOfWork = unitOfWork;
        _translationRoomRepository = _unitOfWork.TranslationRoomRepository;
        _participantRepository = _unitOfWork.TranslationRoomParticipantRepository;
        _workspaceMemberDirectory = workspaceMemberDirectory;
        _redisStateRepository = redisStateRepository;
        _logger = logger;
    }

    public async Task<Result<List<TranslationRoomParticipantDto>>> GetParticipantsAsync(Guid translationRoomId, GetParticipantsRequest request, Guid requestedByUserId, string? requestedByEmail = null, CancellationToken ct = default)
    {
        try
        {
            var room = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (room == null)
                return Result.Failure<List<TranslationRoomParticipantDto>>(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            var requester = await _participantRepository.GetByRoomAndUserAsync(translationRoomId, requestedByUserId, ct);

            // WT-65 loosened this from "must be CONNECTED" to "any participant, any status" —
            // but the whole check was accidentally dropped instead of just the status condition,
            // leaving this endpoint callable by ANY authenticated user for ANY room. Restored with
            // the WT-65 intent kept: host or any participant (regardless of Status) may list
            // participants. ErrorCodes.Forbidden matches this method's own pinned unit test
            // (GetParticipantsAsync_ShouldReturnForbidden_WhenRequesterIsNotInRoom).
            //
            // WT-313 adds the third clause. WT-188 widened *admission* to workspace Owner/Admin but
            // left this read host-or-participant, so an Owner who was neither host nor already a
            // participant was blocked one step earlier than the bug WT-188 set out to fix: the lobby
            // 403'd on its 3s participant poll, so the Approve button WT-188 unlocked was never
            // reached. The check the two share now lives in HasRoomHostAuthorityAsync so they cannot
            // drift apart a third time; the WT-65 participant clause is this read's own widening and
            // stays here.
            //
            // WT-304 adds the fourth clause: a workspace Member invited to this room BY EMAIL who has
            // not yet joined holds no participant row and is not an Owner/Admin, so every clause above
            // says no — permanently. The room detail page still loaded (GET {id} authorizes nothing at
            // all), so the lobby's 3s poll 403'd forever and rendered "Waiting room unavailable". The
            // clause is the invitation, NOT a role: a plain Member with no invitation must still be
            // refused, which GetParticipantsAsync_ShouldReturnForbidden_WhenRequesterIsPlainWorkspaceMember
            // pins.
            //
            // Order is load-bearing, cheapest-and-commonest first:
            //   1. requester != null  — already in hand, no I/O. The poll's normal case.
            //   2. host / Owner-Admin — host is in memory; Owner/Admin costs a gRPC hop.
            //   3. invitation         — a DB round-trip, and only for a caller the first two refused.
            // Appending WT-304 last also means the WT-313 paths reach their verdict through exactly
            // the code they did before, and a caller with no email claim never touches the database.
            if (requester == null
                && !await HasRoomHostAuthorityAsync(room, requestedByUserId, ct)
                && !await HasValidRoomInvitationAsync(translationRoomId, requestedByEmail, ct))
            {
                return Result.Failure<List<TranslationRoomParticipantDto>>(TranslationRoomConstants.ErrorUnauthorizedViewParticipants, ErrorCodes.Forbidden);
            }

            var participants = await _participantRepository.FindAsync(p => p.TranslationRoomId == translationRoomId, "", ct);
            var query = participants.AsEnumerable();

            if (!string.IsNullOrEmpty(request.Search))
            {
                var search = request.Search.ToLower();
                query = query.Where(p => p.DisplayName.ToLower().Contains(search) || (p.UserId != null && p.UserId.Value.ToString().ToLower().Contains(search)));
            }

            if (!string.IsNullOrEmpty(request.Status))
            {
                query = query.Where(p => p.Status.ToString().Equals(request.Status, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrEmpty(request.Role))
            {
                query = query.Where(p => p.Role.Equals(request.Role, StringComparison.OrdinalIgnoreCase));
            }

            query = request.SortBy?.ToLower() switch
            {
                "displayname" => request.IsDescending ? query.OrderByDescending(p => p.DisplayName) : query.OrderBy(p => p.DisplayName),
                "status" => request.IsDescending ? query.OrderByDescending(p => p.Status) : query.OrderBy(p => p.Status),
                "role" => request.IsDescending ? query.OrderByDescending(p => p.Role) : query.OrderBy(p => p.Role),
                _ => request.IsDescending ? query.OrderByDescending(p => p.JoinedAt) : query.OrderBy(p => p.JoinedAt)
            };

            var dtos = query.Select(p => p.ToDto()).ToList();

            return Result.Success(dtos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while getting participants for RoomId: {RoomId}", translationRoomId);
            return Result.Failure<List<TranslationRoomParticipantDto>>("An unexpected error occurred while getting participants.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> UpdateParticipantAudioAsync(Guid translationRoomId, Guid participantId, UpdateParticipantAudioRequest request, Guid requestedByUserId, CancellationToken ct = default)
    {
        try
        {
            var room = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (room == null)
                return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            // WT-313 audit — deliberately left host-only, and NOT folded into
            // HasRoomHostAuthorityAsync. Muting someone's translated audio feed changes what a third
            // party experiences mid-call; unlike listing the lobby it is not recoverable by the
            // person affected, and unlike admitting it is not a step the host is already waiting on
            // someone to perform. The room host is present in the call and accountable for it, so
            // the narrow check is the correct default until a product decision says otherwise.
            //
            // KNOWN GAP, not fixed here (deliberately out of WT-313's scope, which is the read):
            // the web client DOES surface this control to workspace Owners/Admins —
            // persistent-meeting-session.tsx derives `isHost` as
            // `... || role === "admin" || role === "owner"` and people-panel.tsx gates the audio
            // toggle on `canManage = isHost && !isRoomHost`. So an Owner in someone else's room sees
            // the toggle and gets this 403: the same UI/backend mismatch WT-313 is about, one
            // endpoint over. Resolve it by a product decision (widen the endpoint, or hide the
            // control for non-hosts) in its own ticket — do not widen it silently here.
            if (!room.IsHostedBy(requestedByUserId))
                return Result.Failure(TranslationRoomConstants.ErrorOnlyHostCanManageAudio, ErrorCodes.Forbidden);

            var participant = await _participantRepository.GetByIdAsync(participantId, ct);
            if (participant == null || participant.TranslationRoomId != translationRoomId)
                return Result.Failure(TranslationRoomConstants.ErrorParticipantNotFound, ErrorCodes.NotFound);

            // Per BR-1.3-005: "Disable translation audio" means stopping translated audio relay to the participant, not muting their mic.
            participant.IsTranslationAudioEnabled = request.IsTranslationAudioEnabled;
            participant.UpdatedAt = DateTime.UtcNow;

            _participantRepository.Update(participant);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while updating participant audio. RoomId: {RoomId}, ParticipantId: {ParticipantId}", translationRoomId, participantId);
            return Result.Failure(TranslationRoomConstants.ErrorUnexpectedUpdateParticipantAudio, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> AdmitParticipantAsync(Guid translationRoomId, Guid participantId, Guid requestedByUserId, CancellationToken ct = default)
    {
        try
        {
            var room = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (room == null)
                return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            // WT-188: admission is not host-only. The web client already grants host-like lobby
            // controls to workspace Owners/Admins (see room page's `isHost`), so restricting this to
            // room.HostId left them staring at an Approve button that always 403'd — while a plain
            // Member who happened to create the room could admit the Owner. Widened to "room host OR
            // workspace Owner/Admin", which is what the UI has always advertised.
            // WT-313 moved that predicate into HasRoomHostAuthorityAsync, shared with the read side.
            if (!await HasRoomHostAuthorityAsync(room, requestedByUserId, ct))
            {
                return Result.Failure(
                    TranslationRoomConstants.ErrorUnauthorizedAdmitParticipant,
                    ErrorCodes.Forbidden);
            }

            var participant = await _participantRepository.GetByIdAsync(participantId, ct);
            if (participant == null || participant.TranslationRoomId != translationRoomId)
                return Result.Failure("Participant not found.", ErrorCodes.NotFound);

            if (participant.Status != TranslationRoomParticipantStatuses.Waiting)
                return Result.Failure("Participant is not in the waiting room.", ErrorCodes.ValidationError);

            participant.Status = TranslationRoomParticipantStatuses.Connected;
            participant.UpdatedAt = DateTime.UtcNow;

            _participantRepository.Update(participant);
            await _unitOfWork.SaveChangesAsync(ct);

            await PublishParticipantAdmittedAsync(translationRoomId, participant.UserId);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while admitting participant. RoomId: {RoomId}, ParticipantId: {ParticipantId}", translationRoomId, participantId);
            return Result.Failure("An unexpected error occurred while admitting participant.", ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// Tell the admitted client it may come in.
    ///
    /// Approve in the People panel is a REST call that flipped this row and invalidated the HOST's
    /// participants query — nothing reached the admitted user. Their own participant poll is
    /// deliberately disabled while they are in the waiting room and their room query has no
    /// refetch interval, so they sat on the "Waiting for Host" spinner until they happened to press
    /// Refresh Status. It only ever looked like it worked because a host who pressed Start
    /// Translation afterwards triggered RoomStarted, which re-joins everyone; admit with no
    /// subsequent start left the guest stuck indefinitely.
    ///
    /// Same shape as <c>TranslationRoomService.PublishRoomStartedAsync</c>/RoomEnded, for the same
    /// reason: the hub lives in the Gateway process. Published AFTER SaveChangesAsync so a client
    /// that re-joins on the event cannot observe itself still WAITING, and it never throws — an
    /// unnotified guest can still press Refresh Status, but failing the host's Approve after the
    /// row is already CONNECTED would be strictly worse.
    /// </summary>
    private async Task PublishParticipantAdmittedAsync(Guid translationRoomId, Guid? admittedUserId)
    {
        if (_redisStateRepository is null || admittedUserId is null)
        {
            return;
        }

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                Command = ParticipantAdmittedCommand,
                RoomId = translationRoomId.ToString(),
                UserId = admittedUserId.Value.ToString()
            });

            await _redisStateRepository.PublishAsync(GatewayCommandsChannel, payload);
        }
        catch (Exception publishEx)
        {
            _logger.LogError(
                publishEx,
                "Failed to publish ParticipantAdmitted for RoomId: {RoomId}, UserId: {UserId}. The participant is admitted; "
                + "their client will stay on the waiting screen until it retries.",
                translationRoomId,
                admittedUserId);
        }
    }

    public async Task<Result> KickParticipantAsync(Guid translationRoomId, Guid participantId, Guid requestedByUserId, CancellationToken ct = default)
    {
        try
        {
            var room = await _translationRoomRepository.GetByIdAsync(translationRoomId, ct);
            if (room == null)
                return Result.Failure(TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

            // WT-313 audit — deliberately left host-only, and NOT folded into
            // HasRoomHostAuthorityAsync. Kicking is the most privileged act in this file: it is the
            // only one that writes a TERMINAL status. KICKED is not "removed for now" — see
            // ErrorParticipantKicked, the person cannot rejoin the room at all. Admission is
            // reversible by simply admitting again and viewing changes nothing, so the WT-188
            // widening does not transfer here on its own.
            //
            // Unlike the audio gap noted in UpdateParticipantAudioAsync, there is no UI/backend
            // mismatch to trip over: the web client's people-panel "kick" button calls the *meeting*
            // service's LiveKit kick (useKickMeetingParticipant), not this endpoint. This endpoint
            // has no caller in warptalk-web at all, so widening it would be widening authorization
            // on a path nobody is blocked on.
            if (!room.IsHostedBy(requestedByUserId))
                return Result.Failure(TranslationRoomConstants.ErrorOnlyHostCanKick, ErrorCodes.Forbidden);

            var participant = await _participantRepository.GetByIdAsync(participantId, ct);
            if (participant == null || participant.TranslationRoomId != translationRoomId)
                return Result.Failure(TranslationRoomConstants.ErrorParticipantNotFound, ErrorCodes.NotFound);

            // WT-359: protect whoever holds the room NOW. Against room.HostId this shielded a
            // transferred-away host who is an ordinary participant again, while leaving the actual
            // host kickable.
            if (participant.UserId is { } participantUserId && room.IsHostedBy(participantUserId))
                return Result.Failure(TranslationRoomConstants.ErrorCannotKickHost, ErrorCodes.ValidationError);

            participant.Status = TranslationRoomParticipantStatuses.Kicked;
            participant.UpdatedAt = DateTime.UtcNow;

            _participantRepository.Update(participant);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while kicking participant. RoomId: {RoomId}, ParticipantId: {ParticipantId}", translationRoomId, participantId);
            return Result.Failure(TranslationRoomConstants.ErrorUnexpectedKickParticipant, ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result> LeaveRoomAsync(Guid translationRoomId, Guid requestedByUserId, CancellationToken ct = default)
    {
        try
        {
            var participant = await _participantRepository.GetByRoomAndUserAsync(translationRoomId, requestedByUserId, ct);
            if (participant == null)
                return Result.Failure(TranslationRoomConstants.ErrorParticipantNotFound, ErrorCodes.NotFound);

            var leftAt = DateTime.UtcNow;
            participant.Status = TranslationRoomParticipantStatuses.Left;
            participant.LeftAt = leftAt;
            participant.UpdatedAt = leftAt;

            _participantRepository.Update(participant);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while leaving room. RoomId: {RoomId}, UserId: {UserId}", translationRoomId, requestedByUserId);
            return Result.Failure(TranslationRoomConstants.ErrorUnexpectedLeaveRoom, ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// WT-354. A dropped socket is not a departure, and until now it was recorded as one:
    /// ParticipantOfflineConsumerWorker called <see cref="LeaveRoomAsync"/>, which writes the
    /// TERMINAL status LEFT. The People panel hides LEFT rows (people-panel.tsx), so a
    /// backgrounded tab, a laptop going to sleep, a network blip or the one-device kick in
    /// TranslationRoomHub.JoinTranslationRoom deleted a participant from everyone's roster while
    /// they were still in the LiveKit call, speaking — and they never came back to it.
    /// Production carried 182 LEFT rows against 29 DISCONNECTED, which is the shape of a
    /// distinction the write path did not make.
    ///
    /// DISCONNECTED already exists for exactly this and already means "recoverable, distinct from
    /// leaving" on both sides of the wire (see room-occupancy.ts). It releases the seat like LEFT
    /// does, so capacity accounting is unchanged; it simply stops claiming the person went away.
    /// </summary>
    public async Task<Result> MarkParticipantDisconnectedAsync(Guid translationRoomId, Guid requestedByUserId, CancellationToken ct = default)
    {
        try
        {
            var participant = await _participantRepository.GetByRoomAndUserAsync(translationRoomId, requestedByUserId, ct);
            if (participant == null)
                return Result.Failure(TranslationRoomConstants.ErrorParticipantNotFound, ErrorCodes.NotFound);

            // Only a participant who was IN the room can drop out of it. The guard matters most
            // for the ordinary leave: pressing Leave writes LEFT and then closes the socket, so
            // this runs immediately afterwards on every clean departure. Without the guard it
            // would rewrite that LEFT to DISCONNECTED and resurrect the person on the roster —
            // turning the fix into a worse version of the bug.
            //
            // WAITING keeps its existing behaviour on purpose. A lobby row holds no seat and the
            // host is looking at it as a queue; a closed tab should clear it, and marking it
            // DISCONNECTED would leave a phantom request nobody can act on.
            if (participant.Status == TranslationRoomParticipantStatuses.Connected)
            {
                participant.Status = TranslationRoomParticipantStatuses.Disconnected;
                participant.UpdatedAt = DateTime.UtcNow;
                // LeftAt is deliberately untouched: it records a departure, and this is not one.
                // A participant who reconnects goes back to CONNECTED through
                // TranslationRoomService.JoinTranslationRoomAsync, which re-acquires the seat.
            }
            else if (participant.Status == TranslationRoomParticipantStatuses.Waiting)
            {
                var leftAt = DateTime.UtcNow;
                participant.Status = TranslationRoomParticipantStatuses.Left;
                participant.LeftAt = leftAt;
                participant.UpdatedAt = leftAt;
            }
            else
            {
                // Terminal (LEFT / KICKED / REJECTED) or never arrived (INVITED). Nothing a lost
                // socket can say changes any of those.
                return Result.Success();
            }

            _participantRepository.Update(participant);
            await _unitOfWork.SaveChangesAsync(ct);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while marking participant disconnected. RoomId: {RoomId}, UserId: {UserId}", translationRoomId, requestedByUserId);
            return Result.Failure(TranslationRoomConstants.ErrorUnexpectedLeaveRoom, ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// WT-313. The one answer to "does this caller hold host-level authority over this room?":
    /// the room's own host, or an Owner/Admin of the workspace the room belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because the predicate has now drifted twice. WT-188 widened
    /// <see cref="AdmitParticipantAsync"/> from host-only to host-or-Owner/Admin but left
    /// <see cref="GetParticipantsAsync"/> behind, so a workspace Owner was refused the participant
    /// list — one step *before* the Approve button WT-188 had just unlocked, which made the lobby
    /// unusable for exactly the people WT-188 was meant to serve. Two copies of an authorization rule
    /// is the bug; callers must ask this method rather than re-spelling the condition.
    /// </para>
    /// <para>
    /// Not every method here should call this, and that is deliberate — see the audit comments on
    /// <see cref="KickParticipantAsync"/> and <see cref="UpdateParticipantAudioAsync"/> for why those
    /// two stay host-only. What this method removes is *accidental* divergence, not intentional
    /// differences in privilege. <see cref="GetParticipantsAsync"/> additionally admits any existing
    /// participant (WT-65); that widening is specific to the read and stays at its call site.
    /// </para>
    /// <para>
    /// Host identity is checked first so the host path never depends on WorkspaceService being
    /// reachable, and so callers on a polling loop do not make a gRPC hop per request. Pinned by
    /// <c>AdmitParticipantAsync_ShouldAdmit_WhenRequesterIsRoomHost</c>, which asserts
    /// <see cref="IWorkspaceMemberDirectory.IsOwnerOrAdminAsync"/> is never called for a host.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// The rule itself moved to <see cref="RoomHostAccess"/> when TranslationRoomSessionService
    /// needed the same predicate — a private method here was not reusable, which is how these
    /// spellings drift apart in the first place. This stays as the named entry point the comments
    /// above and the pinned tests refer to.
    /// </remarks>
    private Task<bool> HasRoomHostAuthorityAsync(TranslationRoom room, Guid requestedByUserId, CancellationToken ct)
        => RoomHostAccess.HasHostAuthorityAsync(room, requestedByUserId, _workspaceMemberDirectory, ct);

    /// <summary>
    /// WT-304 — "was this caller invited to this room, and does that invitation still stand".
    ///
    /// The rule itself (which invitation states confer read access, and how the email is normalized)
    /// belongs to <see cref="RoomReadAccess"/>, shared with the rooms list and the artifacts/feedback
    /// guard, so the clause cannot drift away from them a fourth time. Only the lookup is local:
    /// this caller already knows the host and participant answers and must not pay to re-ask the
    /// database for them on a 3-second poll.
    ///
    /// A caller with no email claim returns false without touching the database.
    /// </summary>
    private async Task<bool> HasValidRoomInvitationAsync(Guid translationRoomId, string? requestedByEmail, CancellationToken ct)
    {
        var email = RoomReadAccess.NormalizeEmail(requestedByEmail);
        if (email is null)
            return false;

        return await _unitOfWork.TranslationRoomInvitationRepository
            .AnyAsync(RoomReadAccess.GrantsReadOfRoom(translationRoomId, email), ct);
    }
}
