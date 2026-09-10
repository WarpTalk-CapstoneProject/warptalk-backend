using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.TranscriptService.Application.Interfaces;
using GetTranslationRoomRequest = WarpTalk.Shared.Protos.GetTranslationRoomRequest;
using TranslationRoomServiceClient = WarpTalk.Shared.Protos.TranslationRoomService.TranslationRoomServiceClient;

namespace WarpTalk.TranscriptService.Application.Services;

/// <inheritdoc cref="ITranslationRoomEndTime"/>
public sealed class TranslationRoomEndTime : ITranslationRoomEndTime
{
    private readonly TranslationRoomServiceClient _roomClient;
    private readonly ILogger<TranslationRoomEndTime> _logger;

    public TranslationRoomEndTime(
        TranslationRoomServiceClient roomClient,
        ILogger<TranslationRoomEndTime> logger)
    {
        _roomClient = roomClient;
        _logger = logger;
    }

    public async Task<DateTime?> GetEndedAtAsync(
        Guid translationRoomId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var room = await _roomClient.GetTranslationRoomByIdAsync(
                new GetTranslationRoomRequest { Id = translationRoomId.ToString() },
                cancellationToken: cancellationToken);

            // proto3 gives an absent timestamp as "", and a room still in progress genuinely has
            // none — both are "not finished", which is the same answer for this caller's purpose.
            if (string.IsNullOrWhiteSpace(room.EndedAt))
                return null;

            return DateTime.TryParse(
                room.EndedAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var endedAt)
                    ? DateTime.SpecifyKind(endedAt, DateTimeKind.Utc)
                    : null;
        }
        catch (Exception ex)
        {
            // Every failure lands here, NotFound included. See the interface: this exists to make a
            // display detail more honest, and it must never be the reason a transcript panel 500s.
            _logger.LogDebug(ex, "Could not resolve the end time of room {RoomId}", translationRoomId);
            return null;
        }
    }
}
