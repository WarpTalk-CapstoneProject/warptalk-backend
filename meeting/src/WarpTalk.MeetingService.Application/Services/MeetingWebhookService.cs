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
using WarpTalk.Shared.Events;

namespace WarpTalk.MeetingService.Application.Services;

public class MeetingWebhookService : IMeetingWebhookService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IRedisService _redisService;
    private readonly IEgressCompletion _egressCompletion;
    private readonly string _apiSecret;
    private readonly ILogger<MeetingWebhookService> _logger;

    public MeetingWebhookService(
        IUnitOfWork unitOfWork,
        IRedisService redisService,
        IEgressCompletion egressCompletion,
        IConfiguration config,
        ILogger<MeetingWebhookService> logger)
    {
        _unitOfWork = unitOfWork;
        _redisService = redisService;
        _egressCompletion = egressCompletion;
        _apiSecret = config["LiveKit:ApiSecret"] ?? throw new ArgumentNullException("LiveKit:ApiSecret");
        _logger = logger;
    }

    /// <summary>
    /// WT-660: this returns false from four different places, and until now they were
    /// indistinguishable from the outside — the caller turns any of them into one flat 401.
    ///
    /// Production is answering LiveKit 401 on ~105 of 108 webhook deliveries, so no egress_ended
    /// has ever been processed and only the reconciliation sweep has completed a recording. The
    /// investigation could not get past this method: a bad signature, a token with no body hash, a
    /// body that changed in transit and a clock too far out all look identical in the log, and
    /// each points somewhere completely different.
    ///
    /// So each refusal now says which one it was. Deliberately at Warning: a rejected webhook is
    /// not routine, and the whole reason this was expensive to diagnose is that it was silent.
    /// Nothing derived from the secret or the body is logged — the reason is the diagnostic, the
    /// payload is not.
    /// </summary>
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

            if (string.IsNullOrEmpty(sha256Claim))
            {
                _logger.LogWarning(
                    "Rejected a LiveKit webhook: its token carries no 'sha256' body-hash claim. "
                    + "The signature itself was valid, so this is a sender that is not signing the "
                    + "body — check what is posting to this endpoint.");
                return false;
            }

            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(bodyText));
            var computedHash = Convert.ToBase64String(hashBytes);

            if (sha256Claim != computedHash)
            {
                // The signature verified, so the secret is right and the token is LiveKit's. The
                // body therefore is not the one LiveKit hashed: something between them and this
                // method changed it. Length is enough to tell a truncation from a re-encoding, and
                // unlike the body itself it carries no meeting content.
                _logger.LogWarning(
                    "Rejected a LiveKit webhook: the body does not match the hash its token was "
                    + "signed with. The signature was valid, so the secret is correct and the body "
                    + "changed in transit. Received {ByteCount} bytes ({CharCount} chars).",
                    Encoding.UTF8.GetByteCount(bodyText),
                    bodyText.Length);
                return false;
            }

            return true;
        }
        catch (SecurityTokenExpiredException ex)
        {
            // Distinct from a bad signature because the answer is different: LiveKit retries a
            // failed delivery with the ORIGINAL token, so one genuine failure turns into a long
            // tail of expiries that are only a symptom. A first delivery expiring on arrival is
            // the real finding — it means this host's clock is off by more than the 2-minute skew.
            _logger.LogWarning(
                "Rejected a LiveKit webhook: its token had expired ({Expires:o}, now {Now:o}). "
                + "A retry of an already-failed delivery is expected to look like this; a FIRST "
                + "delivery expiring means this host's clock is out by more than the allowed skew.",
                ex.Expires,
                DateTime.UtcNow);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Rejected a LiveKit webhook: its token failed signature validation. If this is "
                + "every delivery, LiveKit is signing with a different secret than "
                + "LiveKit:ApiSecret holds.");
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

        var participant = await _unitOfWork.RtcStreamParticipantRepository
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

        var participant = await _unitOfWork.RtcStreamParticipantRepository
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

    // A completed RoomComposite Egress must not be acknowledged to LiveKit until its durable
    // domain event is in Redis Streams. LiveKit can then retry a transient Redis failure, while
    // the translation-room consumer uses EgressId as its durable idempotency key.
    //
    // The work itself lives in IEgressCompletion because this is no longer the only path into it:
    // EgressReconciliationService performs the identical completion for an egress whose webhook
    // never arrived (WT-371 #8). Two copies would drift, and the copy that drifted would be the
    // fallback — the one that only runs when this path is already broken, so nobody would see it.
    //
    // Still throws on a failed publish: that becomes a 500 and LiveKit retries.
    private async Task HandleEgressEnded(JsonElement root)
    {
        if (!root.TryGetProperty("egressInfo", out var egressInfo))
            return;

        await _egressCompletion.ApplyAsync(egressInfo);
    }

    private async Task HandleTrackPublished(JsonElement root)
    {
        var identity = root.GetProperty("participant").GetProperty("identity").GetString();
        var trackId = root.GetProperty("track").GetProperty("sid").GetString();
        var kind = root.GetProperty("track").GetProperty("kind").GetString();

        var participant = await _unitOfWork.RtcStreamParticipantRepository
            .FirstOrDefaultAsync(p => p.ProviderIdentity == identity);

        if (participant == null) return;

        var track = await _unitOfWork.MeetingTrackRepository
            .FirstOrDefaultAsync(t => t.ProviderTrackId == trackId);

        if (track == null)
        {
            track = new MeetingTrack
            {
                RtcStreamParticipantId = participant.Id,
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
            if (string.IsNullOrWhiteSpace(roomName) || string.IsNullOrWhiteSpace(trackId))
                throw new InvalidOperationException("Audio track webhook is missing room name or track id");

            var envelope = DomainEventEnvelope.Create(
                MeetingEventTypes.TrackPublished,
                "meeting-service",
                workspaceId: null,
                new MeetingTrackPublishedEventPayload(
                    roomName,
                    identity,
                    trackId,
                    DateTime.UtcNow));
            var publishResult = await _redisService.PublishEventAsync(
                MeetingEventTypes.TrackPublished,
                envelope);
            if (!publishResult.IsSuccess)
                throw new InvalidOperationException(
                    $"Could not publish {MeetingEventTypes.TrackPublished}: {publishResult.Error}");
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

        // LiveKit destroys its ephemeral provider room as soon as the last participant
        // disconnects. That is not the same as ending the WarpTalk meeting: the translation
        // room owns the five-minute empty-room grace period and may be rejoined meanwhile.
        // Explicit "End for Everyone" already marks this record FINISHED before DeleteRoom,
        // so a natural room_finished webhook must not advance application lifecycle state.
        if (room != null && string.Equals(
                room.Status,
                MeetingStatus.Finished.ToString(),
                StringComparison.OrdinalIgnoreCase))
        {
            room.EndedAt ??= DateTime.UtcNow;
        }
    }
}
