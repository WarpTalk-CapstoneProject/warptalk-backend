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
        try
        {
            var streamName = "translationRoom:system_events";
            var groupName = "translationRoom_backend_consumer";

            await _redisStreamRepository.EnsureConsumerGroupExistsAsync(streamName, groupName);

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
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred while consuming Redis stream {StreamName}", streamName);
                }

                await Task.Delay(100, stoppingToken); // Small delay to avoid CPU spinning
            }
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "TranslationRoomEventConsumerService background service crashed!");
            throw;
        }
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
