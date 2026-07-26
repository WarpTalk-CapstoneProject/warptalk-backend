using System.Text.Json;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Enums;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.MeetingService.Application.Services;

public class MeetingWebhookService : IMeetingWebhookService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IRedisService _redisService;
    private readonly string _apiSecret;
    private readonly ILogger<MeetingWebhookService> _logger;

    public MeetingWebhookService(IUnitOfWork unitOfWork, IRedisService redisService, IConfiguration config, ILogger<MeetingWebhookService> logger)
    {
        _unitOfWork = unitOfWork;
        _redisService = redisService;
        _apiSecret = config["LiveKit:ApiSecret"] ?? throw new ArgumentNullException("LiveKit:ApiSecret");
        _logger = logger;
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
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(2)
            }, out _);

            // Read the token to verify the body hash (sha256 of body mapped to 'sha256' claim)
            var jwtToken = handler.ReadJwtToken(token);
            var sha256Claim = jwtToken.Claims.FirstOrDefault(c => c.Type == "sha256")?.Value;

            if (string.IsNullOrEmpty(sha256Claim)) return false;

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
                // WT-06: LiveKit's Egress webhook events (exact strings per LiveKit's
                // WebhookEvent — EGRESS_STARTED/EGRESS_UPDATED/EGRESS_ENDED serialize as
                // these lowercase_snake values, mirroring participant_joined/track_published
                // above).
                case "egress_started":
                case "egress_updated":
                    // No DB state change needed for these — ActiveEgressId is already set by
                    // MeetingRoomService.SetRecordingAsync when the host starts recording;
                    // these are informational only.
                    break;
                case "egress_ended":
                    await HandleEgressEnded(root);
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
            participant.IsActive = false; // Add IsActive tracking for webhook disconnect
        }

        // "No Active Host" logic: if the host left, clear the ActiveHostId.
        //
        // WT-08: this intentionally does NOT elect a replacement host — election is owned
        // exclusively by MeetingRoomService.HandleHostOfflineAsync, triggered by the Gateway
        // hub's OnDisconnectedAsync (a separate, connection-level "fully offline" signal from
        // this LiveKit webhook's participant_left). Only clearing here (never assigning a new
        // host) means the two signals can never race to elect DIFFERENT hosts: whichever runs
        // first nulls ActiveHostId (this is idempotent — nulling an already-null value is a
        // no-op), and HandleHostOfflineAsync re-derives "is a host currently assigned" from
        // the DB at the time IT runs, so it correctly elects someone regardless of whether
        // this webhook ran before or after it.
        if (room.ActiveHostId.ToString() == identity)
        {
            room.ActiveHostId = null;
        }
    }

    // WT-06: LiveKit signals a finished RoomComposite Egress here. Clears the room's
    // ActiveEgressId (covers the case where the recording stopped on its own — e.g. the
    // room ended — rather than via an explicit host SetRecordingAsync("stop") call) and
    // notifies the room so the REC indicator/banner disappear for everyone even in that
    // case. Also fires a best-effort "meeting.egress_ended" Redis event carrying the
    // output file location, mirroring the existing "meeting.started"/"meeting.ended"
    // fire-and-forget pattern in MeetingRoomService — for a future translation-room-service
    // consumer to turn into a TranslationRoomArtifact (ArtifactType=OPTIONAL_RECORDING,
    // ContainsRawAudio=true, ContainsRawVideo=true), the same way ArtifactsFinalizer already
    // creates OPTIONAL_RECORDING/TRANSCRIPT_EXPORT/SUMMARY_EXPORT artifacts on room
    // finalization. NOTE: verified this event path is correctly published/shaped, but there
    // is no such consumer wired up in translation-room service yet (out of this round's file
    // scope — translation-room service files aren't touched here) — so today this event is
    // published but nobody creates the artifact row from it. Documented as a known gap.
    private async Task HandleEgressEnded(JsonElement root)
    {
        try
        {
            if (!root.TryGetProperty("egressInfo", out var egressInfo))
                return;

            var egressId = TryGetString(egressInfo, "egressId") ?? TryGetString(egressInfo, "egress_id");
            var roomName = TryGetString(egressInfo, "roomName") ?? TryGetString(egressInfo, "room_name");

            if (string.IsNullOrEmpty(egressId) && string.IsNullOrEmpty(roomName))
                return;

            var room = !string.IsNullOrEmpty(egressId)
                ? await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.ActiveEgressId == egressId)
                : await _unitOfWork.MeetingRoomRepository.FirstOrDefaultAsync(r => r.ProviderRoomName == roomName);

            if (room == null) return;

            room.ActiveEgressId = null;

            string? fileUrl = null;
            if (egressInfo.TryGetProperty("fileResults", out var fileResults) && fileResults.ValueKind == JsonValueKind.Array)
            {
                var first = fileResults.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object)
                    fileUrl = TryGetString(first, "location") ?? TryGetString(first, "filename");
            }

            await _redisService.PublishEventAsync("meeting.egress_ended", new
            {
                TranslationRoomId = room.TranslationRoomId.ToString(),
                EgressId = egressId,
                FileUrl = fileUrl
            });
        }
        catch (Exception ex)
        {
            // Best-effort — a malformed/unexpected egress webhook payload must not fail the
            // whole webhook request (ProcessWebhookAsync's outer catch would otherwise turn
            // this into a 500 for an event we can't act on anyway).
            _logger.LogWarning(ex, "HandleEgressEnded: failed to process egress_ended webhook");
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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
                MediaType = (kind == "video" ? MediaType.Video : MediaType.Audio).ToString(),
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
            room.Status = MeetingStatus.Finished.ToString();
            room.EndedAt = DateTime.UtcNow;
        }
    }
}
