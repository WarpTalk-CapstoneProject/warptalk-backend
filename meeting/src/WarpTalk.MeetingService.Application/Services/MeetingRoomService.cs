using Microsoft.Extensions.Logging;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Services;

public class MeetingRoomService : IMeetingRoomService
{
    private readonly ILiveKitTokenService _tokenService;
    private readonly ITranslationRoomGrpcService _grpcService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IRedisService _redisService;
    private readonly ILogger<MeetingRoomService> _logger;

    public MeetingRoomService(
        ILiveKitTokenService tokenService,
        ITranslationRoomGrpcService grpcService,
        IUnitOfWork unitOfWork,
        IRedisService redisService,
        ILogger<MeetingRoomService> logger)
    {
        _tokenService = tokenService;
        _grpcService = grpcService;
        _unitOfWork = unitOfWork;
        _redisService = redisService;
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
            await _redisService.SetCacheAsync(roomCacheKey, roomDetails, TimeSpan.FromMinutes(5));
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
                await _redisService.PublishEventAsync("meeting.started", new
                {
                    TranslationRoomId = translationRoomId.ToString(),
                    WorkspaceId = roomDetails.WorkspaceId
                });
            }
            else if (meetingRoom.Status != roomDetails.Status)
            {
                meetingRoom.Status = roomDetails.Status;
                _unitOfWork.MeetingRoomRepository.Update(meetingRoom);
                await _unitOfWork.SaveChangesAsync();

                // If it transitions to IN_PROGRESS, might want to trigger too if not done
                if (meetingRoom.Status == "IN_PROGRESS")
                {
                    await _redisService.PublishEventAsync("meeting.started", new
                    {
                        TranslationRoomId = translationRoomId.ToString(),
                        WorkspaceId = roomDetails.WorkspaceId
                    });
                }
            }

            // 3. Enforce Authorization (MeetingInvitation, Expiration & Dynamic Workspace)
            bool isHost = roomDetails.HostId == userIdString;
            bool isAuthorized = isHost;
            Shared.Protos.GetParticipantsByRoomIdResponse? participantsResponse = null;

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

                    if (explicitInvite.ExpiresAt.HasValue && explicitInvite.ExpiresAt.Value < DateTime.UtcNow)
                    {
                        await _unitOfWork.RollbackTransactionAsync();
                        return Result.Failure<JoinMeetingResponse>("Your invitation has expired.", ErrorCodes.Forbidden);
                    }

                    isAuthorized = true;
                }
                else
                {
                    // Fallback to Dynamic Workspace/Group resolution via gRPC
                    var participantsCacheKey = $"meeting:participants:{translationRoomId}";
                    var participantsResult = await _redisService.GetCacheAsync<Shared.Protos.GetParticipantsByRoomIdResponse>(participantsCacheKey);
                    participantsResponse = participantsResult.Value;

                    if (participantsResponse == null)
                    {
                        var grpcPartsResult = await _grpcService.GetParticipantsAsync(translationRoomId);
                        if (grpcPartsResult.IsSuccess && grpcPartsResult.Value != null)
                        {
                            participantsResponse = grpcPartsResult.Value;
                            await _redisService.SetCacheAsync(participantsCacheKey, participantsResponse, TimeSpan.FromMinutes(1));
                        }
                    }

                    if (participantsResponse != null)
                    {
                        var p = participantsResponse.Participants.FirstOrDefault(x => x.Id == userIdString);
                        if (p != null && p.IsActive)
                        {
                            isAuthorized = true;
                        }
                    }
                }
            }

            if (!isAuthorized)
            {
                await _unitOfWork.RollbackTransactionAsync();
                return Result.Failure<JoinMeetingResponse>("You are not authorized to join this meeting.", ErrorCodes.Forbidden);
            }

            // 4. Register or Update Participant
            var providerIdentity = userIdString;
            var participant = await _unitOfWork.MeetingParticipantRepository
                .FirstOrDefaultAsync(p => p.MeetingRoomId == meetingRoom.Id && p.UserId == userId);

            if (participant == null)
            {
                participant = new MeetingParticipant
                {
                    MeetingRoomId = meetingRoom.Id,
                    UserId = userId,
                    ProviderIdentity = providerIdentity,
                    IsActive = true,
                    JoinedAt = DateTime.UtcNow
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
                    await _unitOfWork.CommitTransactionAsync();
                    return Result.Success(new JoinMeetingResponse
                    {
                        Token = string.Empty,
                        ProviderRoomName = meetingRoom.ProviderRoomName,
                        ParticipantIdentity = providerIdentity,
                        IsWaitingRoom = true
                    });
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
                await _redisService.PublishEventAsync("meeting.track_published", new
                {
                    room_name = meetingRoom.ProviderRoomName,
                    participant_identity = providerIdentity,
                    track_sid = "audio_track_1"
                });
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
                IsWaitingRoom = false
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
        await _redisService.PublishEventAsync("meeting.track_published", new
        {
            RoomName = translationRoomId.ToString(),
            ParticipantIdentity = request.ParticipantIdentity,
            TrackId = "audio_track_1"
        });
        return Result.Success<bool>(true);
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

        // 1. Tell Provider (LiveKit) to disconnect them (Worker handles retry)
        await _redisService.PublishEventAsync("meeting.kick_participant", new
        {
            RoomName = meetingRoom.ProviderRoomName,
            ParticipantIdentity = participantUserId.ToString()
        });

        // 2. Tell SignalR Hub to block Chat
        await _redisService.PublishEventAsync("meeting.chat.participant_kicked", new
        {
            RoomId = meetingRoom.Id.ToString(),
            ParticipantUserId = participantUserId.ToString()
        });

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

        // Update status if it exists
        if (meetingRoom != null)
        {
            meetingRoom.Status = "FINISHED";
            meetingRoom.EndedAt = DateTime.UtcNow;
            _unitOfWork.MeetingRoomRepository.Update(meetingRoom);
        }

        await _unitOfWork.SaveChangesAsync();

        // Publish to Provider (LiveKit) to end room
        await _redisService.PublishEventAsync("meeting.end_room", new
        {
            RoomName = meetingRoom?.ProviderRoomName ?? translationRoomId.ToString()
        });

        // Finalize Artifacts and Stop Billing
        await _redisService.PublishEventAsync("meeting.billing.stop", new
        {
            TranslationRoomId = translationRoomId.ToString(),
            MeetingRoomId = meetingRoom?.Id.ToString() ?? Guid.Empty.ToString(),
            WorkspaceId = roomDetails.WorkspaceId
        });

        return Result.Success(true);
    }
}
