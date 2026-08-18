using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Services;

/// <inheritdoc />
public class RoomFlashModeService : IRoomFlashModeService
{
    /// <summary>
    /// The contract with livekit_ingress_worker._flash_mode_enabled. Both halves have to agree on
    /// this string, and the AI side reads it directly from Redis rather than from an API.
    /// </summary>
    private static string KeyFor(Guid roomId) => $"translationRoom:{roomId}:flash_mode";

    /// <summary>
    /// Written as "on"/"off" rather than "true"/"false". The reader accepts both, and this
    /// spelling is what its own log lines and any redis-cli inspection will show.
    /// </summary>
    private const string On = "on";
    private const string Off = "off";

    /// <summary>
    /// Long enough to outlive any meeting, short enough that abandoned rooms do not accumulate
    /// keys forever. Redis here runs allkeys-lru and has evicted live meeting state before, so
    /// nothing this service writes is allowed to be immortal.
    /// </summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    private readonly ITranslationRoomRepository _rooms;
    private readonly ITranslationRoomParticipantRepository _participants;
    private readonly IRedisStateRepository _redis;
    private readonly ILogger<RoomFlashModeService> _logger;

    public RoomFlashModeService(
        ITranslationRoomRepository rooms,
        ITranslationRoomParticipantRepository participants,
        IRedisStateRepository redis,
        ILogger<RoomFlashModeService> logger)
    {
        _rooms = rooms;
        _participants = participants;
        _redis = redis;
        _logger = logger;
    }

    public async Task<Result<bool>> GetAsync(Guid roomId, Guid userId, CancellationToken ct = default)
    {
        // Readable by any participant, so a guest's UI can show the state the host chose rather
        // than guessing. Still gated: this says something about a meeting they must be in.
        var participant = await _participants.GetByRoomAndUserAsync(roomId, userId, ct);
        if (participant == null)
        {
            return Result.Failure<bool>(
                AudioRouteConstants.ErrorParticipantNotInRoom, ErrorCodes.NotFound);
        }

        try
        {
            var raw = await _redis.StringGetAsync(KeyFor(roomId));
            return Result.Success(string.Equals(raw?.Trim(), On, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            // Never an error to the caller. The AI side falls back to the deployment default when
            // it cannot read this key, so the honest answer for a UI is "not on for this room".
            _logger.LogWarning(ex, "Could not read flash mode for room {RoomId}.", roomId);
            return Result.Success(false);
        }
    }

    public async Task<Result<bool>> SetAsync(
        Guid roomId, Guid userId, bool enabled, CancellationToken ct = default)
    {
        var room = await _rooms.GetByIdAsync(roomId, ct);
        if (room == null)
        {
            return Result.Failure<bool>("Room not found.", ErrorCodes.NotFound);
        }

        // IsHostedBy, not "is the creator" — the effective host, which is what survives a host
        // transfer and a host reconnecting. Using the raw creator id here is the exact mistake
        // RoomStartTranslationAccess documents.
        if (!room.IsHostedBy(userId))
        {
            return Result.Failure<bool>(
                "Only the host can change flash mode for this room.", ErrorCodes.Forbidden);
        }

        try
        {
            await _redis.StringSetAsync(KeyFor(roomId), enabled ? On : Off, Ttl);
        }
        catch (Exception ex)
        {
            // Reported rather than swallowed. The person just moved a switch and is about to
            // listen for the difference; telling them it worked when the write failed makes the
            // feature look broken instead of the write.
            _logger.LogError(ex, "Could not write flash mode for room {RoomId}.", roomId);
            return Result.Failure<bool>(
                "Could not change flash mode right now.", ErrorCodes.InternalServerError);
        }

        // Deliberately loud, and at information level: this changes how every speaker in the room
        // is transcribed, and it is the first thing worth knowing when a meeting's transcript
        // quality or latency is being questioned afterwards.
        _logger.LogInformation(
            "Flash mode {State} for room {RoomId} by host {UserId}.",
            enabled ? "ENABLED" : "DISABLED", roomId, userId);

        // No route republish. The ingress worker re-reads this key at each speech onset, so the
        // change reaches the pipeline on its own within seconds — and the route payload is not
        // where this lives (see IRoomFlashModeService).
        return Result.Success(enabled);
    }
}
