using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.API.Workers;

public class ParticipantOfflineConsumerWorker : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ParticipantOfflineConsumerWorker> _logger;

    public ParticipantOfflineConsumerWorker(
        IConnectionMultiplexer redis,
        IServiceProvider serviceProvider,
        ILogger<ParticipantOfflineConsumerWorker> logger)
    {
        _redis = redis;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = _redis.GetSubscriber();
        _logger.LogInformation("ParticipantOfflineConsumerWorker started subscribing to 'translationRoom:participant-offline'.");

        await subscriber.SubscribeAsync("translationRoom:participant-offline", async (channel, message) =>
        {
            try
            {
                var payload = message.ToString();
                if (string.IsNullOrEmpty(payload)) return;

                var parts = payload.Split(':');
                if (parts.Length != 2 || !Guid.TryParse(parts[0], out var roomId) || !Guid.TryParse(parts[1], out var userId))
                {
                    _logger.LogWarning("Invalid participant-offline payload: {Payload}", payload);
                    return;
                }

                _logger.LogInformation("Processing offline event for Room: {RoomId}, User: {UserId}", roomId, userId);

                using var scope = _serviceProvider.CreateScope();
                var participantService = scope.ServiceProvider.GetRequiredService<ITranslationRoomParticipantService>();

                var result = await participantService.LeaveRoomAsync(roomId, userId, stoppingToken);
                if (!result.IsSuccess)
                {
                    _logger.LogWarning("Failed to process LeaveRoom for {UserId} in {RoomId}: {Error}", userId, roomId, result.Error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing participant-offline message");
            }
        });

        // Wait indefinitely until cancellation is requested
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
