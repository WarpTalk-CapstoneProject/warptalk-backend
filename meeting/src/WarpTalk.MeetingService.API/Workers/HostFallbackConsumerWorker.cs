using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.MeetingService.Application.Interfaces;

namespace WarpTalk.MeetingService.API.Workers;

/// <summary>
/// WT-08: subscribes to the SAME "translationRoom:participant-offline" Redis Pub/Sub
/// channel the Gateway's TranslationRoomHub.OnDisconnectedAsync already publishes to
/// unconditionally on every full disconnect (mirrors translation-room service's own
/// ParticipantOfflineConsumerWorker, which consumes the identical channel for a different
/// purpose). This is the ONLY place that elects a new host — see
/// MeetingRoomService.HandleHostOfflineAsync for how this avoids racing with
/// MeetingWebhookService.HandleParticipantLeft (the LiveKit-webhook-driven participant-left
/// path).
/// </summary>
public class HostFallbackConsumerWorker : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<HostFallbackConsumerWorker> _logger;

    public HostFallbackConsumerWorker(
        IConnectionMultiplexer redis,
        IServiceProvider serviceProvider,
        ILogger<HostFallbackConsumerWorker> logger)
    {
        _redis = redis;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = _redis.GetSubscriber();
        _logger.LogInformation("HostFallbackConsumerWorker started subscribing to 'translationRoom:participant-offline'.");

        await subscriber.SubscribeAsync(RedisChannel.Literal("translationRoom:participant-offline"), async (channel, message) =>
        {
            try
            {
                var payload = message.ToString();
                if (string.IsNullOrEmpty(payload)) return;

                var parts = payload.Split(':');
                if (parts.Length != 2 || !Guid.TryParse(parts[0], out var roomId) || !Guid.TryParse(parts[1], out var userId))
                {
                    _logger.LogWarning("HostFallbackConsumerWorker: invalid participant-offline payload: {Payload}", payload);
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var meetingRoomService = scope.ServiceProvider.GetRequiredService<IMeetingRoomService>();

                var result = await meetingRoomService.HandleHostOfflineAsync(roomId, userId);
                if (!result.IsSuccess)
                {
                    _logger.LogWarning("HostFallbackConsumerWorker: HandleHostOfflineAsync failed for room {RoomId}, user {UserId}: {Error}", roomId, userId, result.Error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HostFallbackConsumerWorker: error processing participant-offline message");
            }
        });

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
