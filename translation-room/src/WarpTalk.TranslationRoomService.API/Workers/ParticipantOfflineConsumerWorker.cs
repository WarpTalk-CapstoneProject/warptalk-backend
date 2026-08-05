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

    private const string ParticipantOfflineChannel = "translationRoom:participant-offline";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = _redis.GetSubscriber();

        // The app and infra roles deploy in parallel, so this worker can reach the subscribe
        // call before Redis is accepting connections. An exception escaping ExecuteAsync trips
        // the default BackgroundServiceExceptionBehavior.StopHost and takes the whole service
        // down, which turns a transient Redis blip into a failed deploy. Retry here instead.
        var retryDelay = TimeSpan.FromSeconds(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await subscriber.SubscribeAsync(
                    RedisChannel.Literal(ParticipantOfflineChannel),
                    async (channel, message) => await HandleParticipantOfflineAsync(message, stoppingToken));

                _logger.LogInformation("ParticipantOfflineConsumerWorker started subscribing to '{Channel}'.", ParticipantOfflineChannel);
                break;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "ParticipantOfflineConsumerWorker could not subscribe to '{Channel}'; retrying in {RetryDelay}.", ParticipantOfflineChannel, retryDelay);
                await Task.Delay(retryDelay, stoppingToken);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
            }
        }

        // Wait indefinitely until cancellation is requested
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleParticipantOfflineAsync(RedisValue message, CancellationToken stoppingToken)
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
    }
}
