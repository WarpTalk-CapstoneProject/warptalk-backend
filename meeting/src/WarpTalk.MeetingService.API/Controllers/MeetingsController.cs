using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Infrastructure.Data;

namespace WarpTalk.MeetingService.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class MeetingsController : ControllerBase
{
    private readonly ILiveKitTokenService _tokenService;
    private readonly ITranslationRoomGrpcService _grpcService;
    private readonly MeetingDbContext _dbContext;
    private readonly IRedisService _redisService;
    private readonly ILogger<MeetingsController> _logger;

    public MeetingsController(
        ILiveKitTokenService tokenService, 
        ITranslationRoomGrpcService grpcService, 
        MeetingDbContext dbContext,
        IRedisService redisService,
        ILogger<MeetingsController> logger)
    {
        _tokenService = tokenService;
        _grpcService = grpcService;
        _dbContext = dbContext;
        _redisService = redisService;
        _logger = logger;
    }

    [HttpPost("rooms/{translationRoomId}/join")]
    public async Task<IActionResult> JoinMeeting(Guid translationRoomId)
    {
        var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out var userId))
        {
            return Unauthorized(new { message = "Invalid or missing user identity." });
        }

        // 1. Verify Room Exists via gRPC & Cache
        var roomCacheKey = $"meeting:room:{translationRoomId}";
        var roomDetailsResult = await _redisService.GetCacheAsync<Shared.Protos.GetTranslationRoomResponse>(roomCacheKey);
        var roomDetails = roomDetailsResult.Value;
        
        if (roomDetails == null)
        {
            var grpcResult = await _grpcService.GetRoomDetailsAsync(translationRoomId);
            if (!grpcResult.IsSuccess || grpcResult.Value == null)
                return NotFound(new { message = "Translation room not found" });
            
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
            return StatusCode(403, new { message = "You are not authorized to join this meeting or are still in the waiting room." });
        }

        // 3. Provision / Get Meeting Room
        var meetingRoom = await _dbContext.MeetingRooms
            .FirstOrDefaultAsync(r => r.TranslationRoomId == translationRoomId);

        if (meetingRoom == null)
        {
            meetingRoom = new MeetingRoom
            {
                TranslationRoomId = translationRoomId,
                ProviderRoomName = translationRoomId.ToString()
            };
            _dbContext.MeetingRooms.Add(meetingRoom);
            await _dbContext.SaveChangesAsync();
        }

        // 4. Register Participant
        var providerIdentity = userId.ToString();
        var participant = await _dbContext.MeetingParticipants
            .FirstOrDefaultAsync(p => p.MeetingRoomId == meetingRoom.Id && p.UserId == userId);

        if (participant == null)
        {
            participant = new MeetingParticipant
            {
                MeetingRoomId = meetingRoom.Id,
                UserId = userId,
                ProviderIdentity = providerIdentity
            };
            _dbContext.MeetingParticipants.Add(participant);
            await _dbContext.SaveChangesAsync();
        }

        // 5. Generate Token
        var tokenResult = _tokenService.GenerateToken(
            roomName: meetingRoom.ProviderRoomName,
            participantIdentity: providerIdentity,
            participantName: "User " + userId.ToString().Substring(0, 5),
            canPublish: true,
            canSubscribe: true);

        if (!tokenResult.IsSuccess)
        {
            return StatusCode(500, new { message = tokenResult.Error });
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

        return Ok(new 
        { 
            token = tokenResult.Value, 
            providerRoomName = meetingRoom.ProviderRoomName,
            participantIdentity = providerIdentity
        });
    }

    [HttpPost("rooms/{translationRoomId}/trigger-ai")]
    public async Task<IActionResult> TriggerAi(Guid translationRoomId, [FromBody] TriggerAiRequest req)
    {
        await _redisService.PublishEventAsync("meeting.track_published", new
        {
            RoomName = translationRoomId.ToString(),
            ParticipantIdentity = req.ParticipantIdentity,
            TrackId = "audio_track_1"
        });
        return Ok(new { message = "AI Triggered" });
    }
}
