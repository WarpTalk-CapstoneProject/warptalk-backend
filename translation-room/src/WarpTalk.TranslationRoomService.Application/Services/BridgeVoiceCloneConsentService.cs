using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Services;

/// <inheritdoc />
public class BridgeVoiceCloneConsentService : IBridgeVoiceCloneConsentService
{
    private readonly ITranslationRoomRepository _rooms;
    private readonly IRedisStateRepository _redis;
    private readonly ILogger<BridgeVoiceCloneConsentService> _logger;

    public BridgeVoiceCloneConsentService(
        ITranslationRoomRepository rooms,
        IRedisStateRepository redis,
        ILogger<BridgeVoiceCloneConsentService> logger)
    {
        _rooms = rooms;
        _redis = redis;
        _logger = logger;
    }

    public async Task<Result<BridgeVoiceCloneConsentDto>> SetAsync(
        Guid roomId, Guid userId, string? displayName, bool consented, CancellationToken ct = default)
    {
        // The request is judged before the room is loaded: a blank name is a 400 whoever sent it
        // and whatever the room is, and it costs no query to say so.
        if (displayName is { Length: > FarSpeakerCloneConsent.MaxDisplayNameLength })
        {
            return Result.Failure<BridgeVoiceCloneConsentDto>(
                FarSpeakerCloneConsent.ErrorDisplayNameTooLong, ErrorCodes.ValidationError);
        }

        var field = FarSpeakerCloneConsent.ConsentField(displayName);
        if (field is null)
        {
            return Result.Failure<BridgeVoiceCloneConsentDto>(
                FarSpeakerCloneConsent.ErrorDisplayNameRequired, ErrorCodes.ValidationError);
        }

        var refusal = await RefuseUnlessHostOfOpenBridgeAsync(roomId, userId, ct);
        if (refusal is { } why)
            return Result.Failure<BridgeVoiceCloneConsentDto>(why.Error, why.Code);

        var key = FarSpeakerCloneConsent.KeyFor(roomId);
        var prefix = FarSpeakerCloneConsent.LogPrefix(field);

        try
        {
            if (consented)
            {
                // HSETNX, not HSET: ticking a name that is already ticked must not move the moment
                // the consent was given. The value is the only record of when the host recorded it.
                var written = await _redis.HashSetIfAbsentAsync(
                    key, field, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                await _redis.KeyExpireAsync(key, FarSpeakerCloneConsent.Ttl);

                // Information level on purpose: this is the audit trail of a consent, and the
                // first thing anyone will ask for if a cloned voice is ever disputed.
                _logger.LogInformation(
                    "Far-speaker voice clone consent RECORDED for room {RoomId} by host {UserId}, field {ConsentFieldPrefix} (already in force: {AlreadyInForce}).",
                    roomId, userId, prefix, !written);
            }
            else
            {
                var removed = await _redis.HashDeleteAsync(key, field);

                // A withdrawal is a write too, so the remaining consents get their full day again.
                // On a hash this just emptied the key is already gone and EXPIRE is a no-op.
                await _redis.KeyExpireAsync(key, FarSpeakerCloneConsent.Ttl);

                _logger.LogInformation(
                    "Far-speaker voice clone consent WITHDRAWN for room {RoomId} by host {UserId}, field {ConsentFieldPrefix} (was in force: {WasInForce}).",
                    roomId, userId, prefix, removed);
            }
        }
        catch (Exception ex)
        {
            // Reported rather than swallowed, and most of all for a withdrawal: telling the host
            // "unticked" while the field is still there leaves a voice being cloned that its owner
            // has just said no to.
            _logger.LogError(
                ex,
                "Could not {Action} far-speaker voice clone consent for room {RoomId} by host {UserId}, field {ConsentFieldPrefix}.",
                consented ? "record" : "withdraw", roomId, userId, prefix);
            return Result.Failure<BridgeVoiceCloneConsentDto>(
                FarSpeakerCloneConsent.ErrorCouldNotWrite, ErrorCodes.InternalServerError);
        }

        return Result.Success(new BridgeVoiceCloneConsentDto(displayName!, consented));
    }

    public async Task<Result<BridgeVoiceCloneConsentStatusDto>> GetStatusAsync(
        Guid roomId, Guid userId, IReadOnlyList<string?>? displayNames, CancellationToken ct = default)
    {
        if (displayNames is null)
        {
            return Result.Failure<BridgeVoiceCloneConsentStatusDto>(
                FarSpeakerCloneConsent.ErrorDisplayNamesRequired, ErrorCodes.ValidationError);
        }

        if (displayNames.Count > FarSpeakerCloneConsent.MaxStatusNames)
        {
            return Result.Failure<BridgeVoiceCloneConsentStatusDto>(
                FarSpeakerCloneConsent.ErrorTooManyDisplayNames, ErrorCodes.ValidationError);
        }

        var refusal = await RefuseUnlessHostOfOpenBridgeAsync(roomId, userId, ct);
        if (refusal is { } why)
            return Result.Failure<BridgeVoiceCloneConsentStatusDto>(why.Error, why.Code);

        Dictionary<string, string> fields;
        try
        {
            // One HGETALL rather than up to fifty HGETs: a room's hash holds a handful of fields.
            fields = await _redis.GetHashAllAsync(FarSpeakerCloneConsent.KeyFor(roomId));
        }
        catch (Exception ex)
        {
            // Not answered as "nobody consented": an empty list is a claim about the room, and
            // the host's panel would show every box unticked while voices are being cloned.
            _logger.LogError(
                ex, "Could not read far-speaker voice clone consents for room {RoomId}.", roomId);
            return Result.Failure<BridgeVoiceCloneConsentStatusDto>(
                FarSpeakerCloneConsent.ErrorCouldNotRead, ErrorCodes.InternalServerError);
        }

        // A name that could not have been recorded (blank, or too long) is simply not consented;
        // one odd caption name must not fail the whole roster's question.
        var consented = displayNames
            .Where(name => name is { Length: <= FarSpeakerCloneConsent.MaxDisplayNameLength })
            .Where(name => FarSpeakerCloneConsent.ConsentField(name) is { } field && fields.ContainsKey(field))
            .Select(name => name!)
            .ToList();

        return Result.Success(new BridgeVoiceCloneConsentStatusDto(consented));
    }

    /// <summary>
    /// Null when <paramref name="userId"/> is the effective host of an EXTERNAL_BRIDGE room that
    /// is still open; otherwise why not. The room-shaped answers are the ones the neighbouring
    /// bridge endpoints give (TranslationRoomService.Bridge.LoadOpenBridgeRoomAsync), so a client
    /// handles them the same way.
    /// </summary>
    private async Task<(string Error, string Code)?> RefuseUnlessHostOfOpenBridgeAsync(
        Guid roomId, Guid userId, CancellationToken ct)
    {
        var room = await _rooms.GetByIdAsync(roomId, ct);
        if (room is null || room.DeletedAt is not null)
            return (TranslationRoomConstants.ErrorRoomNotFound, ErrorCodes.NotFound);

        // IsHostedBy — the effective host, which is what survives a Transfer Host — and asked
        // before the room's type and state, so a non-host learns nothing about either.
        if (!room.IsHostedBy(userId))
            return (FarSpeakerCloneConsent.ErrorOnlyHostCanRecord, ErrorCodes.Forbidden);

        if (!TranslationRoomTypes.IsExternalBridge(room.TranslationRoomType))
            return (BridgeRoomConstants.ErrorNotABridgeRoom, ErrorCodes.InvalidState);

        if (BridgeRoomConstants.ClosedStatuses.Contains(room.Status))
            return (BridgeRoomConstants.ErrorRoomClosed, ErrorCodes.InvalidState);

        return null;
    }
}
