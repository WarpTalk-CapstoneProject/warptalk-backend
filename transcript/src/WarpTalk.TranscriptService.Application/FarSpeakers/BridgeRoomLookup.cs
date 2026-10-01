using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using GetTranslationRoomRequest = WarpTalk.Shared.Protos.GetTranslationRoomRequest;
using TranslationRoomServiceClient = WarpTalk.Shared.Protos.TranslationRoomService.TranslationRoomServiceClient;

namespace WarpTalk.TranscriptService.Application.FarSpeakers;

/// <inheritdoc cref="IBridgeRoomLookup"/>
public sealed class BridgeRoomLookup : IBridgeRoomLookup
{
    private readonly TranslationRoomServiceClient _roomClient;
    private readonly ILogger<BridgeRoomLookup> _logger;

    public BridgeRoomLookup(TranslationRoomServiceClient roomClient, ILogger<BridgeRoomLookup> logger)
    {
        _roomClient = roomClient;
        _logger = logger;
    }

    public async Task<(BridgeRoomLookupOutcome Outcome, BridgeRoomInfo? Room)> GetAsync(Guid roomId, CancellationToken ct = default)
    {
        try
        {
            var room = await _roomClient.GetTranslationRoomByIdAsync(
                new GetTranslationRoomRequest { Id = roomId.ToString() },
                cancellationToken: ct);

            return (BridgeRoomLookupOutcome.Found, new BridgeRoomInfo(
                roomId,
                ExternalBridgeConstants.IsBridgeRoomType(room.TranslationRoomType),
                string.IsNullOrWhiteSpace(room.ExternalMeetingUrl) ? null : room.ExternalMeetingUrl,
                // effective_host_id is empty from an older server: fall back to the booker.
                ParseGuid(room.EffectiveHostId) ?? ParseGuid(room.HostId),
                ParseGuid(room.HostId),
                room.Status ?? string.Empty,
                ParseTime(room.StartedAt),
                ParseTime(room.EndedAt)));
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return (BridgeRoomLookupOutcome.NotFound, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read room {RoomId} for far-speaker relabel", roomId);
            return (BridgeRoomLookupOutcome.Unavailable, null);
        }
    }

    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var parsed) ? parsed : null;

    private static DateTime? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
                ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
                : null;
    }
}
