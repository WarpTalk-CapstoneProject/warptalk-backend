using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
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
    /// What livekit_ingress_worker publishes its own deployment default to, every heartbeat.
    ///
    /// The override key above answers "did a host choose something". This answers "what happens
    /// to a room where nobody did" — and without it this service reported "off" for every
    /// untouched room, which was true of the override and false of the room.
    ///
    /// Read rather than mirrored into this service's configuration: the value governs behaviour
    /// on the AI side, and a second setting of the same name in a second service is a setting
    /// that drifts.
    /// </summary>
    private const string DeploymentDefaultKey = "warptalk:stt:flash_mode_default";

    /// <summary>
    /// Written as "on"/"off" rather than "true"/"false". The reader accepts both, and this
    /// spelling is what its own log lines and any redis-cli inspection will show.
    /// </summary>
    private const string On = "on";
    private const string Off = "off";

    /// <summary>
    /// Every spelling livekit_ingress_worker._flash_mode_enabled accepts, so a value set by hand
    /// with redis-cli reads the same on both sides. Anything else is treated as unset rather than
    /// as false — guessing at a typo is how a room ends up configured as nobody intended.
    /// </summary>
    private static readonly string[] TrueSpellings = ["on", "true", "1", "enabled", "yes"];
    private static readonly string[] FalseSpellings = ["off", "false", "0", "disabled", "no"];

    private static bool? Read(string? raw)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (TrueSpellings.Contains(value, StringComparer.OrdinalIgnoreCase)) return true;
        if (FalseSpellings.Contains(value, StringComparer.OrdinalIgnoreCase)) return false;
        return null;
    }

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

    public async Task<Result<FlashModeStateDto>> GetAsync(
        Guid roomId, Guid userId, CancellationToken ct = default)
    {
        // Readable by any participant, so a guest's UI can show the state the host chose rather
        // than guessing. Still gated: this says something about a meeting they must be in.
        var participant = await _participants.GetByRoomAndUserAsync(roomId, userId, ct);
        if (participant == null)
        {
            return Result.Failure<FlashModeStateDto>(
                AudioRouteConstants.ErrorParticipantNotInRoom, ErrorCodes.NotFound);
        }

        try
        {
            // The host's own choice outranks everything, exactly as it does on the AI side.
            var overridden = Read(await _redis.StringGetAsync(KeyFor(roomId)));
            if (overridden.HasValue)
            {
                return Result.Success(new FlashModeStateDto(overridden.Value, FlashModeSources.Room));
            }

            // Nobody chose, so the room does whatever the deployment does — which is the fact
            // this service used to have no way of knowing, and therefore used to report as "off".
            var deployment = Read(await _redis.StringGetAsync(DeploymentDefaultKey));
            if (deployment.HasValue)
            {
                return Result.Success(
                    new FlashModeStateDto(deployment.Value, FlashModeSources.Deployment));
            }

            // No override and no worker has published a default recently. Say so rather than
            // asserting a state: false here is something to render, not something observed.
            return Result.Success(new FlashModeStateDto(false, FlashModeSources.Unknown));
        }
        catch (Exception ex)
        {
            // Never an error to the caller — a switch that cannot be read must not take the
            // meeting panel down with it. But it is reported as unknown rather than as off,
            // because "off" is a claim about the room and this code just failed to make one.
            _logger.LogWarning(ex, "Could not read flash mode for room {RoomId}.", roomId);
            return Result.Success(new FlashModeStateDto(false, FlashModeSources.Unknown));
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
