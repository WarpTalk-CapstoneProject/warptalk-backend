using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Events;

namespace WarpTalk.MeetingService.Application.Services;

public class MeetingRoomService : IMeetingRoomService
{
    private readonly ILiveKitTokenService _tokenService;
    private readonly ITranslationRoomGrpcService _grpcService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IRedisService _redisService;
    private readonly ILiveKitEgressService _egressService;
    private readonly ILiveKitRoomAdminService _roomAdminService;
    private readonly ILogger<MeetingRoomService> _logger;

    // Same Redis Pub/Sub channel TranslationRoomService's WorkspaceEventConsumerWorker
    // already publishes to and the Gateway's TranslationRoomRedisSubscriberService already
    // consumes into TranslationRoomHub — this is the established cross-process mechanism
    // for a non-hub service to push into the Gateway hub. MeetingChatNotifier/AssistantNotifier
    // (IHubContext<THub> injected directly into a service) is a same-process pattern that
    // does not apply here: MeetingRoomService runs in the Meeting microservice, not the
    // Gateway process that owns TranslationRoomHub.
    private const string GatewayCommandsChannel = "warptalk:translation-room:commands";

    public MeetingRoomService(
        ILiveKitTokenService tokenService,
        ITranslationRoomGrpcService grpcService,
        IUnitOfWork unitOfWork,
        IRedisService redisService,
        ILiveKitEgressService egressService,
        ILiveKitRoomAdminService roomAdminService,
        ILogger<MeetingRoomService> logger)
    {
        _tokenService = tokenService;
        _grpcService = grpcService;
        _unitOfWork = unitOfWork;
        _redisService = redisService;
        _egressService = egressService;
        _roomAdminService = roomAdminService;
        _logger = logger;
    }

    public async Task<Result<JoinMeetingResponse>> JoinMeetingAsync(Guid translationRoomId, Guid userId, string? displayName = null)
    {
        var userIdString = userId.ToString();

        // 1. Verify Room Exists via gRPC & Cache
        var roomCacheKey = $"meeting:room:{translationRoomId}";
        var roomDetailsResult = await _redisService.GetCacheAsync<Shared.Protos.GetTranslationRoomResponse>(roomCacheKey);
        var roomDetails = roomDetailsResult.Value;

        if (roomDetails == null)
        {
            var grpcResult = await _grpcService.GetRoomDetailsAsync(translationRoomId);
            if (!grpcResult.IsSuccess || grpcResult.Value == null)
                return Result.Failure<JoinMeetingResponse>("Translation room not found", ErrorCodes.NotFound);

            roomDetails = grpcResult.Value;
            // Billing and AI workers consume this as the local room -> workspace projection.
            // Keep it for the longest supported meeting window instead of expiring mid-call.
            await _redisService.SetCacheAsync(roomCacheKey, roomDetails, TimeSpan.FromHours(24));
        }

        if (roomDetails.Status == "ENDED" || roomDetails.Status == "FINISHED" || roomDetails.Status == "CANCELLED")
        {
            return Result.Failure<JoinMeetingResponse>("This translation room has already ended or been cancelled.", ErrorCodes.InvalidState);
        }

        // Enforce BR-159-015: Scheduled Link Expiration (2 hours)
        if (!string.IsNullOrEmpty(roomDetails.ScheduledStartTime) && DateTime.TryParse(roomDetails.ScheduledStartTime, out var scheduledTime))
        {
            if (DateTime.UtcNow > scheduledTime.AddHours(2))
            {
                return Result.Failure<JoinMeetingResponse>("This meeting link has expired.", ErrorCodes.InvalidState);
            }
        }

        await _unitOfWork.BeginTransactionAsync();
        try
        {
        // 2. Provision / Get Meeting Room
        var meetingRoom = await _unitOfWork.MeetingRoomRepository
            .FirstOrDefaultAsync(r => r.TranslationRoomId == translationRoomId);

        if (meetingRoom == null)
        {
            meetingRoom = new MeetingRoom
            {
                TranslationRoomId = translationRoomId,
                ProviderRoomName = translationRoomId.ToString(),
                Status = roomDetails.Status
            };
            await _unitOfWork.MeetingRoomRepository.AddAsync(meetingRoom);
            await _unitOfWork.SaveChangesAsync();

            // Notify WorkspaceService to capture Context Snapshot
            await PublishMeetingStartedAsync(
                translationRoomId,
                roomDetails.WorkspaceId,
                roomDetails.Title,
                roomDetails.Description);
        }
        else if (meetingRoom.Status != roomDetails.Status)
        {
            meetingRoom.Status = roomDetails.Status;
            _unitOfWork.MeetingRoomRepository.Update(meetingRoom);
            await _unitOfWork.SaveChangesAsync();

            // If it transitions to IN_PROGRESS, might want to trigger too if not done
            if (meetingRoom.Status == "IN_PROGRESS")
            {
                await PublishMeetingStartedAsync(
                    translationRoomId,
                    roomDetails.WorkspaceId,
                    roomDetails.Title,
                    roomDetails.Description);
            }
        }

        // 3. Enforce Authorization (MeetingInvitation, Expiration & Dynamic Workspace)
        bool isHost = roomDetails.HostId == userIdString;
        bool isAuthorized = isHost;
        Shared.Protos.GetParticipantsByRoomIdResponse? participantsResponse = null;

        // WT-04: a locked room rejects everyone except the host and participants who are
        // already active in it (e.g. a reconnect after a network blip must not be treated
        // as a new-joiner lockout) — checked ahead of the authorization gate below so a
        // locked room reports "Room is locked." rather than a generic authorization error
        // even to someone who would otherwise have been authorized to join. Only queries
        // for the caller's existing participant row when the room IS locked, to avoid an
        // extra DB round-trip on the (common) non-locked path — step 4 below falls back to
        // its own query when this stays null.
        MeetingParticipant? existingParticipant = null;
        if (meetingRoom.IsLocked)
        {
            existingParticipant = await _unitOfWork.MeetingParticipantRepository
                .FirstOrDefaultAsync(p => p.MeetingRoomId == meetingRoom.Id && p.UserId == userId);
            bool isExistingActiveParticipant = existingParticipant != null && existingParticipant.IsActive && !existingParticipant.LeftAt.HasValue;
            if (!isHost && !isExistingActiveParticipant)
            {
                await _unitOfWork.RollbackTransactionAsync();
                return Result.Failure<JoinMeetingResponse>("Room is locked.", ErrorCodes.Forbidden);
            }
        }

        if (!isHost)
        {
            // Check MeetingInvitation Table first (for explicit invites & external guests)
            var invitationRepo = _unitOfWork.Repository<MeetingInvitation>();
            var explicitInvite = await invitationRepo.FirstOrDefaultAsync(i => i.MeetingRoomId == meetingRoom.Id && i.InviteeUserId == userId);

            if (explicitInvite != null)
            {
                if (explicitInvite.Status == "REVOKED")
                {
                    await _unitOfWork.RollbackTransactionAsync();
                    return Result.Failure<JoinMeetingResponse>("Your invitation has been revoked.", ErrorCodes.Forbidden);
                }

                if (explicitInvite.Status == "DECLINED")
                {
                    await _unitOfWork.RollbackTransactionAsync();
                    return Result.Failure<JoinMeetingResponse>("You have declined this invitation.", ErrorCodes.Forbidden);
                }

                if (explicitInvite.ExpiresAt.HasValue && explicitInvite.ExpiresAt.Value < DateTime.UtcNow)
                {
                    await _unitOfWork.RollbackTransactionAsync();
                    return Result.Failure<JoinMeetingResponse>("Your invitation has expired.", ErrorCodes.Forbidden);
                }

                // A PENDING invite is implicitly accepted by the act of joining.
                if (explicitInvite.Status == "PENDING")
                {
                    explicitInvite.Status = "ACCEPTED";
                    invitationRepo.Update(explicitInvite);
                }

                isAuthorized = true;
            }
            else
            {
                // Authenticated user room authorization
                isAuthorized = true;
            }
        }

        if (!isAuthorized)
        {
            await _unitOfWork.RollbackTransactionAsync();
            return Result.Failure<JoinMeetingResponse>("You are not authorized to join this meeting.", ErrorCodes.Forbidden);
        }

        // 4. Register or Update Participant
        var providerIdentity = userIdString;
        var participant = existingParticipant ?? await _unitOfWork.MeetingParticipantRepository
            .FirstOrDefaultAsync(p => p.MeetingRoomId == meetingRoom.Id && p.UserId == userId);

        if (participant == null)
        {
            participant = new MeetingParticipant
            {
                Id = Guid.CreateVersion7(),
                MeetingRoomId = meetingRoom.Id,
                UserId = userId,
                ProviderIdentity = providerIdentity,
                IsActive = true,
                JoinedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            await _unitOfWork.MeetingParticipantRepository.AddAsync(participant);
            await _unitOfWork.SaveChangesAsync();
        }
        else
        {
            if (!participant.IsActive || participant.LeftAt.HasValue)
            {
                participant.IsActive = true;
                participant.JoinedAt = DateTime.UtcNow;
                participant.LeftAt = null;
                _unitOfWork.MeetingParticipantRepository.Update(participant);
                await _unitOfWork.SaveChangesAsync();
            }
        }

        // 5. Lobby / Waiting Room Logic
        if (meetingRoom.Status == "SCHEDULED" || meetingRoom.Status == "WAITING")
        {
            if (isHost)
            {
                meetingRoom.ActiveHostId = userId;
                _unitOfWork.MeetingRoomRepository.Update(meetingRoom);
                await _unitOfWork.SaveChangesAsync();
            }
            else
            {
                // Translation Room owns lobby admission. Once the host admits this user,
                // its participant row becomes CONNECTED and is exposed over gRPC as active.
                // Do not keep an admitted participant trapped in Meeting Service's lobby
                // just because the room itself is still in WAITING state.
                if (participantsResponse == null)
                {
                    var grpcPartsResult = await _grpcService.GetParticipantsAsync(translationRoomId);
                    if (grpcPartsResult.IsSuccess && grpcPartsResult.Value != null)
                        participantsResponse = grpcPartsResult.Value;
                }

                var translationParticipant = participantsResponse?.Participants
                    .FirstOrDefault(p => p.Id == userIdString);

                if (translationParticipant?.IsActive != true)
                {
                    await _unitOfWork.CommitTransactionAsync();
                    return Result.Success(new JoinMeetingResponse
                    {
                        Token = string.Empty,
                        ProviderRoomName = meetingRoom.ProviderRoomName,
                        ParticipantIdentity = providerIdentity,
                        IsWaitingRoom = true,
                        MuteOnEntry = meetingRoom.MuteOnEntry
                    });
                }
            }
        }
        else if (isHost && meetingRoom.ActiveHostId == null)
        {
            meetingRoom.ActiveHostId = userId;
            _unitOfWork.MeetingRoomRepository.Update(meetingRoom);
            await _unitOfWork.SaveChangesAsync();
        }

        // 6. Resolve participant display name for LiveKit token
        // Priority: 1) displayName from controller (from JWT/frontend), 2) gRPC participant lookup, 3) fallback
        string participantName = displayName ?? "Participant";

        // If no displayName was provided, reuse participants already fetched during auth (or fetch once)
        if (string.IsNullOrEmpty(displayName))
        {
            try
            {
                if (participantsResponse == null)
                {
                    var grpcPartsResult = await _grpcService.GetParticipantsAsync(translationRoomId);
                    if (grpcPartsResult.IsSuccess && grpcPartsResult.Value != null)
                        participantsResponse = grpcPartsResult.Value;
                }
                if (participantsResponse != null)
                {
                    var p = participantsResponse.Participants.FirstOrDefault(x => x.Id == userIdString);
                    if (p != null && !string.IsNullOrEmpty(p.DisplayName))
                        participantName = p.DisplayName;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve participant display name via gRPC");
            }
        }

        var tokenResult = _tokenService.GenerateToken(
            roomName: meetingRoom.ProviderRoomName,
            participantIdentity: providerIdentity,
            participantName: participantName,
            canPublish: true,
            canSubscribe: true);

        if (!tokenResult.IsSuccess)
        {
            await _unitOfWork.RollbackTransactionAsync();
            return Result.Failure<JoinMeetingResponse>(tokenResult.Error ?? "Failed to generate token", ErrorCodes.InternalServerError);
        }

        await _unitOfWork.CommitTransactionAsync();

        // 7. Notify AI Worker via Redis Pub/Sub
        try
        {
            await PublishTrackPublishedAsync(
                meetingRoom.ProviderRoomName,
                providerIdentity,
                "audio_track_1");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-trigger AI worker for room {RoomName}", meetingRoom.ProviderRoomName);
        }

        return Result.Success(new JoinMeetingResponse
        {
            Token = tokenResult.Value!,
            ProviderRoomName = meetingRoom.ProviderRoomName,
            ParticipantIdentity = providerIdentity,
            IsWaitingRoom = false,
            MuteOnEntry = meetingRoom.MuteOnEntry
        });
        } // end try (BeginTransactionAsync)
        catch (Exception ex)
        {
            await _unitOfWork.RollbackTransactionAsync();
            _logger.LogError(ex, "Unexpected error in JoinMeetingAsync for room {RoomId}", translationRoomId);
            return Result.Failure<JoinMeetingResponse>("An unexpected error occurred while joining the meeting.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<bool>> TriggerAiAsync(Guid translationRoomId, TriggerAiRequest request)
    {
        // Re-publish meeting context when translation is explicitly started. The initial
        // meeting.started Pub/Sub notification is intentionally lightweight but can be
        // missed across a consumer restart; this idempotent write lets TranscriptService
        // restore the STT/MT prompt before the first deliberate translation turn.
        var roomDetailsResult = await _grpcService.GetRoomDetailsAsync(translationRoomId);
        if (roomDetailsResult.IsSuccess && roomDetailsResult.Value != null)
        {
            try
            {
                await PublishMeetingStartedAsync(
                    translationRoomId,
                    roomDetailsResult.Value.WorkspaceId,
                    roomDetailsResult.Value.Title,
                    roomDetailsResult.Value.Description);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to republish meeting context before starting AI for room {RoomId}",
                    translationRoomId);
                return Result.Failure<bool>(
                    "Failed to prepare meeting context.",
                    ErrorCodes.InternalServerError);
            }
        }
        else
        {
            _logger.LogWarning(
                "Could not refresh meeting context before starting AI for room {RoomId}: {Error}",
                translationRoomId,
                roomDetailsResult.Error);
        }

        var publishResult = await PublishTrackPublishedAsync(
            translationRoomId.ToString(),
            request.ParticipantIdentity,
            "audio_track_1");

        if (!publishResult.IsSuccess)
        {
            return Result.Failure<bool>(
                publishResult.Error ?? "Failed to publish AI trigger event",
                publishResult.ErrorCode ?? ErrorCodes.InternalServerError);
        }

        return Result.Success<bool>(true);
    }

    private async Task PublishMeetingStartedAsync(
        Guid translationRoomId,
        string workspaceIdValue,
        string? title,
        string? description)
    {
        if (!Guid.TryParse(workspaceIdValue, out var workspaceId))
            throw new InvalidOperationException(
                $"Cannot publish {MeetingEventTypes.Started}: invalid workspace id");

        var envelope = DomainEventEnvelope.Create(
            MeetingEventTypes.Started,
            "meeting-service",
            workspaceId.ToString(),
            new MeetingStartedEventPayload(
                translationRoomId,
                workspaceId,
                title,
                description));
        var result = await _redisService.PublishEventAsync(
            MeetingEventTypes.Started,
            envelope);

        if (!result.IsSuccess)
            throw new InvalidOperationException(
                $"Cannot publish {MeetingEventTypes.Started}: {result.Error}");
    }

    private Task<Result> PublishTrackPublishedAsync(
        string roomName,
        string? participantIdentity,
        string trackId)
    {
        var envelope = DomainEventEnvelope.Create(
            MeetingEventTypes.TrackPublished,
            "meeting-service",
            workspaceId: null,
            new MeetingTrackPublishedEventPayload(
                roomName,
                participantIdentity,
                trackId,
                DateTime.UtcNow));
        return _redisService.PublishEventAsync(
            MeetingEventTypes.TrackPublished,
            envelope);
    }

    public async Task<Result<bool>> RejectParticipantAsync(Guid translationRoomId, Guid hostUserId, Guid participantUserId)
    {
        var hostIdString = hostUserId.ToString();

        // 1. Verify Host Authorization
        var roomCacheKey = $"meeting:room:{translationRoomId}";
        var roomDetailsResult = await _redisService.GetCacheAsync<Shared.Protos.GetTranslationRoomResponse>(roomCacheKey);
        var roomDetails = roomDetailsResult.Value;

        if (roomDetails == null)
        {
            var grpcResult = await _grpcService.GetRoomDetailsAsync(translationRoomId);
            if (!grpcResult.IsSuccess || grpcResult.Value == null)
                return Result.Failure<bool>("Translation room not found", ErrorCodes.NotFound);

            roomDetails = grpcResult.Value;
        }

        // 2. Get Meeting Room (needed for ActiveHostId check)
        var meetingRoom = await _unitOfWork.MeetingRoomRepository
            .FirstOrDefaultAsync(r => r.TranslationRoomId == translationRoomId);

        if (meetingRoom == null)
            return Result.Failure<bool>("Meeting room not started.", ErrorCodes.NotFound);

        bool isOriginalHost = roomDetails.HostId == hostIdString;
        bool isActiveHost = meetingRoom.ActiveHostId == hostUserId;

        if (!isOriginalHost && !isActiveHost)
        {
            return Result.Failure<bool>("Only the host can reject participants.", ErrorCodes.Forbidden);
        }

        // 3. Revoke Invitation
        var invitationRepo = _unitOfWork.Repository<MeetingInvitation>();
        var invitation = await invitationRepo.FirstOrDefaultAsync(i => i.MeetingRoomId == meetingRoom.Id && i.InviteeUserId == participantUserId);

        if (invitation != null)
        {
            invitation.Status = "REVOKED";
            invitationRepo.Update(invitation);
        }
        else
        {
            // Create a revoked invitation to prevent future joins
            invitation = new MeetingInvitation
            {
                MeetingRoomId = meetingRoom.Id,
                InviteeUserId = participantUserId,
                Status = "REVOKED",
                WorkspaceId = Guid.TryParse(roomDetails.WorkspaceId, out var wsIdReject) ? wsIdReject : Guid.Empty
            };
            await invitationRepo.AddAsync(invitation);
        }

        // 4. Update Participant state
        var participant = await _unitOfWork.MeetingParticipantRepository
            .FirstOrDefaultAsync(p => p.MeetingRoomId == meetingRoom.Id && p.UserId == participantUserId);

        if (participant != null)
        {
            participant.IsActive = false;
            participant.LeftAt = DateTime.UtcNow;
            _unitOfWork.MeetingParticipantRepository.Update(participant);
        }

        await _unitOfWork.SaveChangesAsync();

        // Optional: Send event to disconnect them if they are connected (via LiveKit API)
        // For Lobby presence, this DB update is enough to reject them from the waiting list.

        return Result.Success(true);
    }

    public async Task<Result<bool>> TransferHostAsync(Guid translationRoomId, Guid currentHostUserId, Guid newHostUserId)
    {
        var meetingRoom = await _unitOfWork.MeetingRoomRepository
            .FirstOrDefaultAsync(r => r.TranslationRoomId == translationRoomId);

        if (meetingRoom == null)
            return Result.Failure<bool>("Meeting room not found.", ErrorCodes.NotFound);

        // Check if the current user is the Active Host OR the Original Host
        var roomCacheKey = $"meeting:room:{translationRoomId}";
        var roomDetailsResult = await _redisService.GetCacheAsync<Shared.Protos.GetTranslationRoomResponse>(roomCacheKey);
        var roomDetails = roomDetailsResult.Value;

        if (roomDetails == null)
        {
            var grpcResult = await _grpcService.GetRoomDetailsAsync(translationRoomId);
            if (!grpcResult.IsSuccess || grpcResult.Value == null)
                return Result.Failure<bool>("Translation room not found.", ErrorCodes.NotFound);

            roomDetails = grpcResult.Value;
        }

        bool isOriginalHost = roomDetails.HostId == currentHostUserId.ToString();
        bool isActiveHost = meetingRoom.ActiveHostId == currentHostUserId;

        if (!isOriginalHost && !isActiveHost)
        {
            return Result.Failure<bool>("You are not authorized to transfer host.", ErrorCodes.Forbidden);
        }

        // Verify new host is an active participant
        var newHostParticipant = await _unitOfWork.MeetingParticipantRepository
            .FirstOrDefaultAsync(p => p.MeetingRoomId == meetingRoom.Id && p.UserId == newHostUserId && p.IsActive);

        if (newHostParticipant == null)
        {
            return Result.Failure<bool>("The new host must be an active participant in the meeting.", ErrorCodes.ValidationError);
        }

        meetingRoom.ActiveHostId = newHostUserId;
        _unitOfWork.MeetingRoomRepository.Update(meetingRoom);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success(true);
    }

    public async Task<Result<bool>> KickParticipantAsync(Guid translationRoomId, Guid hostUserId, Guid participantUserId)
    {
        var meetingRoom = await _unitOfWork.MeetingRoomRepository
            .FirstOrDefaultAsync(r => r.TranslationRoomId == translationRoomId);

        if (meetingRoom == null)
            return Result.Failure<bool>("Meeting room not found.", ErrorCodes.NotFound);

        // Authorization
        var roomCacheKey = $"meeting:room:{translationRoomId}";
        var roomDetailsResult = await _redisService.GetCacheAsync<Shared.Protos.GetTranslationRoomResponse>(roomCacheKey);
        var roomDetails = roomDetailsResult.Value;

        if (roomDetails == null)
        {
            var grpcResult = await _grpcService.GetRoomDetailsAsync(translationRoomId);
            if (!grpcResult.IsSuccess || grpcResult.Value == null)
                return Result.Failure<bool>("Translation room not found.", ErrorCodes.NotFound);
            roomDetails = grpcResult.Value;
        }

        bool isOriginalHost = roomDetails.HostId == hostUserId.ToString();
        bool isActiveHost = meetingRoom.ActiveHostId == hostUserId;

        if (!isOriginalHost && !isActiveHost)
            return Result.Failure<bool>("Only the host can kick participants.", ErrorCodes.Forbidden);

        // Update Participant status
        var participant = await _unitOfWork.MeetingParticipantRepository
            .FirstOrDefaultAsync(p => p.MeetingRoomId == meetingRoom.Id && p.UserId == participantUserId);

        if (participant != null)
        {
            participant.IsActive = false;
            participant.LeftAt = DateTime.UtcNow;
            _unitOfWork.MeetingParticipantRepository.Update(participant);
        }

        // Revoke Invitation to prevent re-join
        var invitationRepo = _unitOfWork.Repository<MeetingInvitation>();
        var invitation = await invitationRepo.FirstOrDefaultAsync(i => i.MeetingRoomId == meetingRoom.Id && i.InviteeUserId == participantUserId);

        if (invitation != null)
        {
            invitation.Status = "REVOKED";
            invitationRepo.Update(invitation);
        }
        else
        {
            await invitationRepo.AddAsync(new MeetingInvitation
            {
                MeetingRoomId = meetingRoom.Id,
                InviteeUserId = participantUserId,
                Status = "REVOKED",
                WorkspaceId = Guid.TryParse(roomDetails.WorkspaceId, out var wsIdKick) ? wsIdKick : Guid.Empty
            });
        }

        await _unitOfWork.SaveChangesAsync();

        var removeResult = await _roomAdminService.RemoveParticipantAsync(
            meetingRoom.ProviderRoomName,
            participantUserId.ToString());
        if (!removeResult.IsSuccess)
            return Result.Failure<bool>(
                removeResult.Error ?? "Failed to remove participant from LiveKit.",
                removeResult.ErrorCode);

        return Result.Success(true);
    }

    public async Task<Result<bool>> EndMeetingAsync(Guid translationRoomId, Guid hostUserId)
    {
        // 1. Fetch Room Details for Authorization
        var roomCacheKey = $"meeting:room:{translationRoomId}";
        var roomDetailsResult = await _redisService.GetCacheAsync<Shared.Protos.GetTranslationRoomResponse>(roomCacheKey);
        var roomDetails = roomDetailsResult.Value;

        if (roomDetails == null)
        {
            var grpcResult = await _grpcService.GetRoomDetailsAsync(translationRoomId);
            if (!grpcResult.IsSuccess || grpcResult.Value == null)
                return Result.Failure<bool>("Translation room not found.", ErrorCodes.NotFound);
            roomDetails = grpcResult.Value;
        }

        var meetingRoom = await _unitOfWork.MeetingRoomRepository
            .FirstOrDefaultAsync(r => r.TranslationRoomId == translationRoomId);

        bool isOriginalHost = roomDetails.HostId == hostUserId.ToString();
        bool isActiveHost = meetingRoom?.ActiveHostId == hostUserId;

        if (!isOriginalHost && !isActiveHost)
            return Result.Failure<bool>("Only the host can end the meeting for all.", ErrorCodes.Forbidden);

        // Provider teardown must succeed before application state is finalized. Otherwise a
        // transient LiveKit authorization/configuration error leaves the DB saying FINISHED
        // while participants are still connected to a live provider room.
        var deleteRoomResult = await _roomAdminService.DeleteRoomAsync(
            meetingRoom?.ProviderRoomName ?? translationRoomId.ToString());
        if (!deleteRoomResult.IsSuccess)
            return Result.Failure<bool>(
                deleteRoomResult.Error ?? "Failed to end LiveKit room.",
                deleteRoomResult.ErrorCode);

        if (meetingRoom != null)
        {
            meetingRoom.Status = "FINISHED";
            meetingRoom.EndedAt = DateTime.UtcNow;
            _unitOfWork.MeetingRoomRepository.Update(meetingRoom);
        }

        await _unitOfWork.SaveChangesAsync();

        // WT-13: Trigger AI meeting-summary generation. The Python AI Assistant worker
        // (warptalk-ai/ai_assistant_worker) already accumulates the meeting transcript from
        // stt:results and generates a summary + action items when it sees a sentinel
        // "__MEETING_END__" text segment (see AIAssistantWorker.process/_generate_summary) —
        // this mirrors that exact existing async-worker trigger instead of adding a new one.
        // Best-effort: a failed publish must not fail EndMeetingAsync itself.
        try
        {
            await _redisService.PublishStreamMessageAsync("stt:results", new Dictionary<string, string>
            {
                ["segment_id"] = Guid.NewGuid().ToString(),
                ["meeting_id"] = translationRoomId.ToString(),
                ["speaker_id"] = "system",
                ["text"] = "__MEETING_END__",
                ["language"] = "system",
                ["confidence"] = "1",
                ["start_ms"] = "0",
                ["end_ms"] = "0",
                ["chunk_index"] = "0",
                ["is_final_chunk"] = "1",
                ["timestamp_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to trigger AI summary generation for room {RoomId}", translationRoomId);
        }

        return Result.Success(true);
    }

    // ── WT-04: Host controls ──────────────────────────────

    public async Task<Result<bool>> SetLockAsync(Guid translationRoomId, Guid callerUserId, bool locked)
    {
        var meetingRoom = await _unitOfWork.MeetingRoomRepository
            .FirstOrDefaultAsync(r => r.TranslationRoomId == translationRoomId);
        if (meetingRoom == null)
            return Result.Failure<bool>("Meeting room not found.", ErrorCodes.NotFound);

        if (!await IsHostAsync(translationRoomId, meetingRoom, callerUserId))
            return Result.Failure<bool>("Only the host can lock or unlock the room.", ErrorCodes.Forbidden);

        meetingRoom.IsLocked = locked;
        _unitOfWork.MeetingRoomRepository.Update(meetingRoom);
        await _unitOfWork.SaveChangesAsync();

        await PublishGatewayCommandAsync("RoomLockChanged", translationRoomId, new { Locked = locked });

        return Result.Success(true);
    }

    public async Task<Result<bool>> SetMuteOnEntryAsync(Guid translationRoomId, Guid callerUserId, bool muteOnEntry)
    {
        var meetingRoom = await _unitOfWork.MeetingRoomRepository
            .FirstOrDefaultAsync(r => r.TranslationRoomId == translationRoomId);
        if (meetingRoom == null)
            return Result.Failure<bool>("Meeting room not found.", ErrorCodes.NotFound);

        if (!await IsHostAsync(translationRoomId, meetingRoom, callerUserId))
            return Result.Failure<bool>("Only the host can change mute-on-entry.", ErrorCodes.Forbidden);

        meetingRoom.MuteOnEntry = muteOnEntry;
        _unitOfWork.MeetingRoomRepository.Update(meetingRoom);
        await _unitOfWork.SaveChangesAsync();

        // Known gap: this only affects the NEXT joiner (read off JoinMeetingResponse.MuteOnEntry
        // — see JoinMeetingAsync). It is not broadcast live to the room like RoomLockChanged/
        // RecordingStateChanged, so a host with a second open tab won't see this toggle update
        // there. A real fix would add a "MuteOnEntryChanged" broadcast the same way as the other
        // two settings; left out to keep this change minimal, since it doesn't affect anyone
        // already in the meeting.
        return Result.Success(true);
    }

    // ── WT-06: Recording via LiveKit Egress ───────────────

    public async Task<Result<RecordingStateDto>> SetRecordingAsync(Guid translationRoomId, Guid callerUserId, string action)
    {
        var meetingRoom = await _unitOfWork.MeetingRoomRepository
            .FirstOrDefaultAsync(r => r.TranslationRoomId == translationRoomId);
        if (meetingRoom == null)
            return Result.Failure<RecordingStateDto>("Meeting room not found.", ErrorCodes.NotFound);

        if (!await IsHostAsync(translationRoomId, meetingRoom, callerUserId))
            return Result.Failure<RecordingStateDto>("Only the host can control recording.", ErrorCodes.Forbidden);

        var normalizedAction = action?.Trim().ToLowerInvariant();

        if (normalizedAction == "start")
        {
            if (!string.IsNullOrEmpty(meetingRoom.ActiveEgressId))
                return Result.Failure<RecordingStateDto>("Recording is already in progress.", ErrorCodes.InvalidState);

            var startResult = await _egressService.StartRoomCompositeEgressAsync(meetingRoom.ProviderRoomName);
            if (!startResult.IsSuccess || string.IsNullOrEmpty(startResult.Value))
                return Result.Failure<RecordingStateDto>(startResult.Error ?? "Failed to start recording.", ErrorCodes.InternalServerError);

            meetingRoom.ActiveEgressId = startResult.Value;
            _unitOfWork.MeetingRoomRepository.Update(meetingRoom);
            await _unitOfWork.SaveChangesAsync();

            await PublishGatewayCommandAsync("RecordingStateChanged", translationRoomId, new { Recording = true });

            return Result.Success(new RecordingStateDto { Recording = true, EgressId = meetingRoom.ActiveEgressId });
        }

        if (normalizedAction == "stop")
        {
            if (string.IsNullOrEmpty(meetingRoom.ActiveEgressId))
                return Result.Failure<RecordingStateDto>("No recording is currently in progress.", ErrorCodes.InvalidState);

            var stopResult = await _egressService.StopEgressAsync(meetingRoom.ActiveEgressId);
            if (!stopResult.IsSuccess)
                return Result.Failure<RecordingStateDto>(stopResult.Error ?? "Failed to stop recording.", ErrorCodes.InternalServerError);

            meetingRoom.ActiveEgressId = null;
            _unitOfWork.MeetingRoomRepository.Update(meetingRoom);
            await _unitOfWork.SaveChangesAsync();

            await PublishGatewayCommandAsync("RecordingStateChanged", translationRoomId, new { Recording = false });

            return Result.Success(new RecordingStateDto { Recording = false, EgressId = null });
        }

        return Result.Failure<RecordingStateDto>("Action must be 'start' or 'stop'.", ErrorCodes.ValidationError);
    }

    // ── WT-08: Auto host fallback ──────────────────────────

    /// <summary>
    /// Authoritative host-fallback election. Triggered ONLY from the Gateway hub's
    /// OnDisconnectedAsync full-disconnect signal (via HostFallbackConsumerWorker
    /// subscribing to the same "translationRoom:participant-offline" channel the hub
    /// already publishes to unconditionally — see TranslationRoomHub.OnDisconnectedAsync).
    ///
    /// MeetingWebhookService.HandleParticipantLeft (the OTHER participant-left signal,
    /// from LiveKit's webhook) intentionally does NOT elect a new host — it only clears
    /// ActiveHostId when the departing identity matches it, which is safe/idempotent to run
    /// in either order relative to this method: whichever runs first "wins" the null-out,
    /// and the equality check there means it never clobbers a host this method has already
    /// elected. This method is the only place a NON-NULL ActiveHostId is ever assigned as a
    /// fallback, so there is no double-election race between the two paths. It re-derives
    /// "should I elect someone" from current DB state rather than trusting the webhook to
    /// have already run, so it works correctly regardless of which of the two signals
    /// arrives first.
    /// </summary>
    public async Task<Result<bool>> HandleHostOfflineAsync(Guid translationRoomId, Guid departedUserId)
    {
        var meetingRoom = await _unitOfWork.MeetingRoomRepository
            .FirstOrDefaultAsync(r => r.TranslationRoomId == translationRoomId);
        if (meetingRoom == null)
            return Result.Success(false);

        bool departedWasHost = meetingRoom.ActiveHostId == departedUserId;
        // Nothing to do if some other, still-current host is already assigned.
        if (!departedWasHost && meetingRoom.ActiveHostId != null)
            return Result.Success(false);

        var activeParticipants = await _unitOfWork.MeetingParticipantRepository
            .FindAsync(p => p.MeetingRoomId == meetingRoom.Id && p.IsActive && p.UserId != departedUserId);

        var nextHost = activeParticipants.OrderBy(p => p.JoinedAt).FirstOrDefault();

        // No active participants left: fall back to "no host", matching
        // MeetingWebhookService.HandleParticipantLeft's existing null-out behavior.
        meetingRoom.ActiveHostId = nextHost?.UserId;
        _unitOfWork.MeetingRoomRepository.Update(meetingRoom);
        await _unitOfWork.SaveChangesAsync();

        if (nextHost != null)
        {
            await PublishGatewayCommandAsync("HostChanged", translationRoomId, new { NewHostUserId = nextHost.UserId.ToString() });
        }

        return Result.Success(true);
    }

    // ── Helpers ────────────────────────────────────────────

    private async Task<bool> IsHostAsync(Guid translationRoomId, MeetingRoom meetingRoom, Guid callerUserId)
    {
        var roomCacheKey = $"meeting:room:{translationRoomId}";
        var roomDetailsResult = await _redisService.GetCacheAsync<Shared.Protos.GetTranslationRoomResponse>(roomCacheKey);
        var roomDetails = roomDetailsResult.Value;

        if (roomDetails == null)
        {
            var grpcResult = await _grpcService.GetRoomDetailsAsync(translationRoomId);
            if (!grpcResult.IsSuccess || grpcResult.Value == null)
                return false;
            roomDetails = grpcResult.Value;
        }

        bool isOriginalHost = roomDetails.HostId == callerUserId.ToString();
        bool isActiveHost = meetingRoom.ActiveHostId == callerUserId;
        return isOriginalHost || isActiveHost;
    }

    private Task<Result> PublishGatewayCommandAsync(string command, Guid translationRoomId, object extraFields)
    {
        var payload = new Dictionary<string, object?>
        {
            ["Command"] = command,
            ["RoomId"] = translationRoomId.ToString()
        };

        foreach (var property in extraFields.GetType().GetProperties())
        {
            payload[property.Name] = property.GetValue(extraFields);
        }

        return _redisService.PublishEventAsync(GatewayCommandsChannel, payload);
    }
}
