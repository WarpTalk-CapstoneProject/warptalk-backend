using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.Infrastructure.Redis;

public class TelemetryRedisSubscriber : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelemetryRedisSubscriber> _logger;
    private const string TelemetryChannel = "translationRoom:telemetry";

    public TelemetryRedisSubscriber(
        IConnectionMultiplexer redis,
        IServiceScopeFactory scopeFactory,
        ILogger<TelemetryRedisSubscriber> logger)
    {
        _redis = redis;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TelemetryRedisSubscriber starting, subscribing to: {Channel}", TelemetryChannel);

        var subscriber = _redis.GetSubscriber();

        // GUARDED: this used to catch, LogCritical and then RETHROW, which is not a guard at all —
        // the rethrow still trips the default BackgroundServiceExceptionBehavior.StopHost and
        // takes the whole TranslationRoomService process down over telemetry, the least critical
        // thing this service does. Retry with bounded backoff instead, the same shape as
        // ParticipantOfflineConsumerWorker in this service's API assembly.
        var retryDelay = TimeSpan.FromSeconds(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await subscriber.SubscribeAsync(RedisChannel.Literal(TelemetryChannel), async (channel, val) =>
                {
                    try
                    {
                        var payloadStr = val.ToString();
                        _logger.LogDebug("Received telemetry payload: {Payload}", payloadStr);

                        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        var dto = JsonSerializer.Deserialize<TelemetryPayload>(payloadStr, options);

                        if (dto != null && dto.RoomId != Guid.Empty)
                        {
                            if (dto.Timestamp <= 0)
                            {
                                dto.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            }

                            using var scope = _scopeFactory.CreateScope();
                            var telemetryProcessor = scope.ServiceProvider.GetRequiredService<ITelemetryProcessor>();

                            await telemetryProcessor.ProcessTelemetryAsync(dto, stoppingToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing incoming telemetry message from Redis");
                    }
                });

                _logger.LogInformation("TelemetryRedisSubscriber subscribed to {Channel}.", TelemetryChannel);
                break;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(
                    ex,
                    "TelemetryRedisSubscriber could not subscribe to {Channel}; retrying in {RetryDelay}. "
                    + "Room telemetry is not being collected until it succeeds.",
                    TelemetryChannel,
                    retryDelay);
                await Task.Delay(retryDelay, stoppingToken);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
            }
        }

        // Loop until cancelled to keep the background service alive
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        finally
        {
            await subscriber.UnsubscribeAsync(RedisChannel.Literal(TelemetryChannel));
            _logger.LogInformation("TelemetryRedisSubscriber unsubscribed from {Channel} and stopped.", TelemetryChannel);
        }
    }
}
