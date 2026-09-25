using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;

namespace WarpTalk.BillingService.Infrastructure.Clients;

/// <summary>LiveKit usage from translation-room (GetMediaUsage RPC) for the admin Providers page.</summary>
public sealed class MediaUsageGrpcClient : IMediaUsageClient
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    private readonly TranslationRoomService.TranslationRoomServiceClient _client;
    private readonly ILogger<MediaUsageGrpcClient> _logger;

    public MediaUsageGrpcClient(TranslationRoomService.TranslationRoomServiceClient client, ILogger<MediaUsageGrpcClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<MediaUsageHourRow>>> GetHourlyAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetMediaUsageAsync(
                new GetMediaUsageRequest
                {
                    FromUnix = new DateTimeOffset(DateTime.SpecifyKind(from, DateTimeKind.Utc)).ToUnixTimeSeconds(),
                    ToUnix = new DateTimeOffset(DateTime.SpecifyKind(to, DateTimeKind.Utc)).ToUnixTimeSeconds(),
                },
                deadline: DateTime.UtcNow.Add(Deadline),
                cancellationToken: ct);

            IReadOnlyList<MediaUsageHourRow> rows = response.Hours
                .Select(h => new MediaUsageHourRow(
                    DateTimeOffset.FromUnixTimeSeconds(h.HourStartUnix).UtcDateTime,
                    Guid.TryParse(h.WorkspaceId, out var workspace) ? workspace : Guid.Empty,
                    h.RoomSeconds,
                    h.ParticipantSeconds,
                    h.RoomsStarted,
                    h.Recordings,
                    h.RecordingBytes))
                .ToList();
            return Result.Success(rows);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LiveKit media usage could not be read from translation-room.");
            return Result.Failure<IReadOnlyList<MediaUsageHourRow>>("translation-room did not answer the media usage request", ErrorCodes.ServiceUnavailable);
        }
    }
}
