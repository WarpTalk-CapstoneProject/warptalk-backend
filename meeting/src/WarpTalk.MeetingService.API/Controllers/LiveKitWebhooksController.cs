using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using WarpTalk.MeetingService.Infrastructure.Data;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Enums;
using WarpTalk.MeetingService.Application.Interfaces;

namespace WarpTalk.MeetingService.API.Controllers;

[ApiController]
[Route("api/meetings/webhooks/livekit")]
public class LiveKitWebhooksController : ControllerBase
{
    private readonly MeetingDbContext _dbContext;
    private readonly string _apiSecret;
    private readonly IRedisService _redisService;

    public LiveKitWebhooksController(MeetingDbContext dbContext, IConfiguration config, IRedisService redisService)
    {
        _dbContext = dbContext;
        _apiSecret = config["LiveKit:ApiSecret"] ?? throw new ArgumentNullException("LiveKit:ApiSecret");
        _redisService = redisService;
    }

    [HttpPost]
    [AllowAnonymous] // Verification is done via LiveKit token in header
    public async Task<IActionResult> HandleWebhook()
    {
        // 1. Read Body
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var bodyText = await reader.ReadToEndAsync();

        // 2. Validate Signature
        var authHeader = Request.Headers["Authorization"].ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            return Unauthorized("Missing or invalid Authorization header");

        var token = authHeader.Substring("Bearer ".Length);
        if (!ValidateWebhookToken(token, bodyText))
            return Unauthorized("Invalid webhook signature");

        // 3. Process Event
        using var doc = JsonDocument.Parse(bodyText);
        var root = doc.RootElement;
        
        if (!root.TryGetProperty("event", out var eventProperty))
            return BadRequest("Missing event type");

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

            await _dbContext.SaveChangesAsync();
            return Ok();
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }

    private bool ValidateWebhookToken(string token, string bodyText)
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

    private async Task HandleParticipantJoined(JsonElement root)
    {
        var roomName = root.GetProperty("room").GetProperty("name").GetString();
        var identity = root.GetProperty("participant").GetProperty("identity").GetString();

        var room = await _dbContext.MeetingRooms.FirstOrDefaultAsync(r => r.ProviderRoomName == roomName);
        if (room == null) return;

        var participant = await _dbContext.MeetingParticipants
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

        var room = await _dbContext.MeetingRooms.FirstOrDefaultAsync(r => r.ProviderRoomName == roomName);
        if (room == null) return;

        var participant = await _dbContext.MeetingParticipants
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

        var participant = await _dbContext.MeetingParticipants
            .FirstOrDefaultAsync(p => p.ProviderIdentity == identity);

        if (participant == null) return;

        var track = await _dbContext.MeetingTracks
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
            _dbContext.MeetingTracks.Add(track);
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
        var track = await _dbContext.MeetingTracks.FirstOrDefaultAsync(t => t.ProviderTrackId == trackId);
        
        if (track != null)
        {
            track.UnpublishedAt = DateTime.UtcNow;
        }
    }

    private async Task HandleTrackMuted(JsonElement root, bool isMuted)
    {
        var trackId = root.GetProperty("track").GetProperty("sid").GetString();
        var track = await _dbContext.MeetingTracks.FirstOrDefaultAsync(t => t.ProviderTrackId == trackId);
        
        if (track != null)
        {
            track.IsMuted = isMuted;
        }
    }

    private async Task HandleRoomFinished(JsonElement root)
    {
        var roomName = root.GetProperty("room").GetProperty("name").GetString();
        var room = await _dbContext.MeetingRooms.FirstOrDefaultAsync(r => r.ProviderRoomName == roomName);
        
        if (room != null)
        {
            room.Status = MeetingStatus.Finished;
            room.EndedAt = DateTime.UtcNow;
        }
    }
}
