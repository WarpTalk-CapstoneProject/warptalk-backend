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

    public async Task<Result<JoinMeetingResponse>> JoinMeetingAsync(Guid translationRoomId, Guid userId)
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

        // 2. Enforce Authorization (Host or CONNECTED participant)
        bool isAuthorized = false;

        if (roomDetails.HostId == userIdString)
        {
            isAuthorized = true;
        }
        else
        {
            var participantsCacheKey = $"meeting:participants:{translationRoomId}";
            var participantsResult = await _redisService.GetCacheAsync<Shared.Protos.GetParticipantsByRoomIdResponse>(participantsCacheKey);
            var participantsResponse = participantsResult.Value;

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

        if (!isAuthorized)
        {
            return Result.Failure<JoinMeetingResponse>("You are not authorized to join this meeting or are still in the waiting room.", ErrorCodes.Forbidden);
        }

        // 3. Provision / Get Meeting Room
        var meetingRoom = await _unitOfWork.MeetingRoomRepository
            .FirstOrDefaultAsync(r => r.TranslationRoomId == translationRoomId);

        if (meetingRoom == null)
        {
            meetingRoom = new MeetingRoom
            {
                TranslationRoomId = translationRoomId,
                ProviderRoomName = translationRoomId.ToString()
            };
            await _unitOfWork.MeetingRoomRepository.AddAsync(meetingRoom);
            await _unitOfWork.SaveChangesAsync();
        }

        // 4. Register Participant
        var providerIdentity = userIdString;
        var participant = await _unitOfWork.MeetingParticipantRepository
            .FirstOrDefaultAsync(p => p.MeetingRoomId == meetingRoom.Id && p.UserId == userId);

        if (participant == null)
        {
            participant = new MeetingParticipant
            {
                MeetingRoomId = meetingRoom.Id,
                UserId = userId,
                ProviderIdentity = providerIdentity
            };
            await _unitOfWork.MeetingParticipantRepository.AddAsync(participant);
            await _unitOfWork.SaveChangesAsync();
        }

        // 5. Generate Token
        var tokenResult = _tokenService.GenerateToken(
            roomName: meetingRoom.ProviderRoomName,
            participantIdentity: providerIdentity,
            participantName: "User " + userIdString.Substring(0, 5),
            canPublish: true,
            canSubscribe: true);

        if (!tokenResult.IsSuccess)
        {
            return Result.Failure<JoinMeetingResponse>(tokenResult.Error ?? "Failed to generate token", ErrorCodes.InternalServerError);
        }

        // 6. Notify AI Worker via Redis Pub/Sub
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

        return Result.Success<JoinMeetingResponse>(new JoinMeetingResponse
        {
            Token = tokenResult.Value!,
            ProviderRoomName = meetingRoom.ProviderRoomName,
            ParticipantIdentity = providerIdentity
        });
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
}
