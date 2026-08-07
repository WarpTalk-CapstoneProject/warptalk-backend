using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.Infrastructure.Redis;

public class TranslationRoomEventConsumerService : BackgroundService
{
    private readonly IRedisStreamRepository _redisStreamRepository;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TranslationRoomEventConsumerService> _logger;

    public TranslationRoomEventConsumerService(
        IRedisStreamRepository redisStreamRepository,
        IServiceScopeFactory scopeFactory,
        ILogger<TranslationRoomEventConsumerService> logger)
    {
        _redisStreamRepository = redisStreamRepository;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var streamName = "translationRoom:system_events";
        var groupName = "translationRoom_backend_consumer";

        // GUARDED: group creation used to sit under a catch that logged Critical and rethrew,
        // so an unreachable Redis tripped BackgroundServiceExceptionBehavior.StopHost and took
        // TranslationRoomService down instead of just this consumer. Retries with bounded
        // backoff so consumption resumes on its own once Redis returns.
        if (!await EnsureConsumerGroupAsync(streamName, groupName, stoppingToken))
            return;

        var consumerName = $"backend-{Environment.MachineName}-{Guid.NewGuid().ToString("N")[..8]}";
        _logger.LogInformation("Starting Redis stream consumer with name: {ConsumerName}", consumerName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var messages = await _redisStreamRepository.ReadGroupAsync(
                    streamName,
                    groupName,
                    consumerName,
                    ">",
                    count: 10);

                foreach (var message in messages)
                {
                    await ProcessMessageWithRetryAsync(message, stoppingToken);
                    await _redisStreamRepository.AcknowledgeAsync(streamName, groupName, message.Id);
                }

                await Task.Delay(100, stoppingToken); // Small delay to avoid CPU spinning
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown. Previously the Task.Delay below sat outside this try, so a
                // normal stop surfaced as "background service crashed!" at Critical.
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while consuming Redis stream {StreamName}", streamName);
            }
        }
    }

    /// <returns>true once the group exists; false only when the host is shutting down.</returns>
    private async Task<bool> EnsureConsumerGroupAsync(
        string streamName,
        string groupName,
        CancellationToken ct)
    {
        var retryDelay = TimeSpan.FromSeconds(2);
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _redisStreamRepository.EnsureConsumerGroupExistsAsync(streamName, groupName);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                attempt++;
                _logger.LogError(
                    ex,
                    "TranslationRoomEventConsumerService could not create consumer group {Group} on {Stream} "
                    + "(attempt {Attempt}); retrying in {RetryDelay}. Room system events are NOT being "
                    + "processed until it succeeds.",
                    groupName, streamName, attempt, retryDelay);

                try
                {
                    await Task.Delay(retryDelay, ct);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }

                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
            }
        }

        return false;
    }

    private async Task ProcessMessageWithRetryAsync(RedisStreamMessage message, CancellationToken ct)
    {
        int maxRetries = 3;
        int attempt = 0;
        bool isProcessed = false;
        string lastError = "Unknown error";

        var eventTypeStr = message.Values.GetValueOrDefault("event_type", "");
        var routeIdStr = message.Values.GetValueOrDefault("route_id", "");
        var roomIdStr = message.Values.GetValueOrDefault("room_id", "");
        var payloadStr = message.Values.GetValueOrDefault("payload", "");

        while (attempt < maxRetries && !isProcessed)
        {
            attempt++;
            try
            {
                if (Guid.TryParse(roomIdStr, out var roomId))
                {
                    Guid? routeId = Guid.TryParse(routeIdStr, out var parsedRouteId) ? parsedRouteId : null;

                    using var scope = _scopeFactory.CreateScope();
                    var processorService = scope.ServiceProvider.GetRequiredService<IAudioRouteEventProcessor>();

                    var result = await processorService.ProcessEventAsync(roomId, routeId, eventTypeStr, payloadStr, ct);

                    if (result.IsSuccess)
                    {
                        isProcessed = true;
                    }
                    else
                    {
                        lastError = result.Error ?? "Unknown event processing error";
                        _logger.LogWarning("Attempt {Attempt}/{MaxRetries} failed to process event {EventType} for room {RoomId}. Error: {Error}",
                            attempt, maxRetries, eventTypeStr, roomId, lastError);
                    }
                }
                else
                {
                    lastError = "Invalid RoomId format";
                    break; // Không retry nếu RoomId bị sai định dạng
                }
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                _logger.LogWarning(ex, "Attempt {Attempt}/{MaxRetries} threw exception processing stream message {MessageId}",
                    attempt, maxRetries, message.Id);
            }

            if (!isProcessed && attempt < maxRetries)
            {
                // Exponential backoff
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);
            }
        }

        if (!isProcessed)
        {
            _logger.LogError("Message {MessageId} failed after {MaxRetries} attempts. Routing to DLQ.", message.Id, maxRetries);
            await RouteToDlqAsync(message, lastError, ct);
        }
    }

    private async Task RouteToDlqAsync(RedisStreamMessage message, string error, CancellationToken ct)
    {
        try
        {
            var dlqStream = "translationRoom:system_events:dlq";

            var dlqValues = new Dictionary<string, string>(message.Values, StringComparer.OrdinalIgnoreCase)
            {
                ["original_message_id"] = message.Id,
                ["error_message"] = error,
                ["failed_at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()
            };

            await _redisStreamRepository.AddAsync(dlqStream, dlqValues);
            _logger.LogInformation("Successfully routed message {MessageId} to DLQ stream {DlqStream}", message.Id, dlqStream);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to push message {MessageId} to DLQ stream.", message.Id);
        }
    }
}
