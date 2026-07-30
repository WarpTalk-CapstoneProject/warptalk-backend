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
        try
        {
            _logger.LogInformation("TelemetryRedisSubscriber starting, subscribing to: {Channel}", TelemetryChannel);

            var subscriber = _redis.GetSubscriber();

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
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "TelemetryRedisSubscriber background service crashed!");
            throw;
        }
    }
}
