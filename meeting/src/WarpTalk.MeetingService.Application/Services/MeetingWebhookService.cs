using System.Text.Json;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Enums;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Services;

public class MeetingWebhookService : IMeetingWebhookService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IRedisService _redisService;
    private readonly string _apiSecret;

    public MeetingWebhookService(IUnitOfWork unitOfWork, IRedisService redisService, IConfiguration config)
    {
        _unitOfWork = unitOfWork;
        _redisService = redisService;
        _apiSecret = config["LiveKit:ApiSecret"] ?? throw new ArgumentNullException("LiveKit:ApiSecret");
    }

    public bool ValidateWebhookToken(string token, string bodyText)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_apiSecret));
            
            handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = securityKey,
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = false // Sometimes webhooks are slightly delayed
            }, out _);

            // Read the token to verify the body hash (sha256 of body mapped to 'sha256' claim)
            var jwtToken = handler.ReadJwtToken(token);
            var sha256Claim = jwtToken.Claims.FirstOrDefault(c => c.Type == "sha256")?.Value;

            if (string.IsNullOrEmpty(sha256Claim)) return true; // Some older LiveKit versions omit this

            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(bodyText));
            var computedHash = Convert.ToBase64String(hashBytes);

            return sha256Claim == computedHash;
        }
        catch
        {
            return false;
        }
    }

    public async Task<Result<bool>> ProcessWebhookAsync(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var eventProperty))
            return Result.Failure<bool>("Missing event type", ErrorCodes.ValidationError);

        var eventType = eventProperty.GetString();

        try
        {
            switch (eventType)
            {
                case "participant_joined":
                    await HandleParticipantJoined(root);
                    break;
                case "participant_left":
                    await HandleParticipantLeft(root);
                    break;
                case "track_published":
                    await HandleTrackPublished(root);
                    break;
                case "track_unpublished":
                    await HandleTrackUnpublished(root);
                    break;
                case "track_muted":
                    await HandleTrackMuted(root, true);
                    break;
                case "track_unmuted":
                    await HandleTrackMuted(root, false);
                    break;
                case "room_finished":
                    await HandleRoomFinished(root);
                    break;
            }

            await _unitOfWork.SaveChangesAsync();
            return Result.Success<bool>(true);
        }
        catch (Exception ex)
        {
            return Result.Failure<bool>(ex.Message, ErrorCodes.InternalServerError);
        }
    }

    private async Task HandleParticipantJoined(JsonElement root)
    {
        var roomName = root.GetProperty("room").GetProperty("name").GetString();
        var identity = root.GetProperty("participant").GetProperty("identity").GetString();

        var room = await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.ProviderRoomName == roomName);
        if (room == null) return;

        var participant = await _unitOfWork.MeetingParticipantRepository
            .FirstOrDefaultAsync(p => p.MeetingRoomId == room.Id && p.ProviderIdentity == identity);

        if (participant != null)
        {
            participant.JoinedAt = DateTime.UtcNow;
            participant.LeftAt = null;
        }
    }

    private async Task HandleParticipantLeft(JsonElement root)
    {
        var roomName = root.GetProperty("room").GetProperty("name").GetString();
        var identity = root.GetProperty("participant").GetProperty("identity").GetString();

        var room = await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.ProviderRoomName == roomName);
        if (room == null) return;

        var participant = await _unitOfWork.MeetingParticipantRepository
            .FirstOrDefaultAsync(p => p.MeetingRoomId == room.Id && p.ProviderIdentity == identity);

        if (participant != null)
        {
            participant.LeftAt = DateTime.UtcNow;
        }
    }

    private async Task HandleTrackPublished(JsonElement root)
    {
        var identity = root.GetProperty("participant").GetProperty("identity").GetString();
        var trackId = root.GetProperty("track").GetProperty("sid").GetString();
        var kind = root.GetProperty("track").GetProperty("kind").GetString();

        var participant = await _unitOfWork.MeetingParticipantRepository
            .FirstOrDefaultAsync(p => p.ProviderIdentity == identity);

        if (participant == null) return;

        var track = await _unitOfWork.MeetingTrackRepository
            .FirstOrDefaultAsync(t => t.ProviderTrackId == trackId);

        if (track == null)
        {
            track = new MeetingTrack
            {
                MeetingParticipantId = participant.Id,
                ProviderTrackId = trackId ?? string.Empty,
                MediaType = kind == "video" ? MediaType.Video : MediaType.Audio,
                PublishedAt = DateTime.UtcNow
            };
            await _unitOfWork.MeetingTrackRepository.AddAsync(track);
        }
        else
        {
            track.UnpublishedAt = null;
        }
        
        // Publish to Redis Pub/Sub for Transcript Worker to start
        if (kind == "audio")
        {
            var roomName = root.GetProperty("room").GetProperty("name").GetString();
            await _redisService.PublishEventAsync("meeting.track_published", new
            {
                RoomName = roomName,
                ParticipantIdentity = identity,
                TrackId = trackId,
                Timestamp = DateTime.UtcNow
            });
        }
    }

    private async Task HandleTrackUnpublished(JsonElement root)
    {
        var trackId = root.GetProperty("track").GetProperty("sid").GetString();
        var track = await _unitOfWork.MeetingTrackRepository.FirstOrDefaultAsync(t => t.ProviderTrackId == trackId);
        
        if (track != null)
        {
            track.UnpublishedAt = DateTime.UtcNow;
        }
    }

    private async Task HandleTrackMuted(JsonElement root, bool isMuted)
    {
        var trackId = root.GetProperty("track").GetProperty("sid").GetString();
        var track = await _unitOfWork.MeetingTrackRepository.FirstOrDefaultAsync(t => t.ProviderTrackId == trackId);
        
        if (track != null)
        {
            track.IsMuted = isMuted;
        }
    }

    private async Task HandleRoomFinished(JsonElement root)
    {
        var roomName = root.GetProperty("room").GetProperty("name").GetString();
        var room = await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.ProviderRoomName == roomName);
        
        if (room != null)
        {
            room.Status = MeetingStatus.Finished;
            room.EndedAt = DateTime.UtcNow;
        }
    }
}
