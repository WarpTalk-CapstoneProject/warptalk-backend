using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.Infrastructure.Redis;

public class TranslationRoomEventConsumerService : BackgroundService
{
    private readonly IConnectionMultiplexer _redisConnection;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TranslationRoomEventConsumerService> _logger;

    public TranslationRoomEventConsumerService(
        IConnectionMultiplexer redisConnection,
        IServiceScopeFactory scopeFactory,
        ILogger<TranslationRoomEventConsumerService> logger)
    {
        _redisConnection = redisConnection;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Subscribe to a single stream or pub/sub channel for all room system events.
            // For simplicity and alignment with existing spec, let's assume we consume from a pub/sub channel
            // or a Redis stream. The spec says "Redis Stream: translationRoom:{roomId}:system_events".
            // Consuming dynamically from multiple streams requires a more complex stream reader (e.g. XREADGROUP across many keys).
            // Since this is a capstone, we will implement a basic stream reading loop or listen to a global stream 
            // if possible. Alternatively, using Pub/Sub here simplifies it if we don't need strict consumer group features right now.
            // The spec explicitly says "Redis Streams". We'll use a global stream `translationRoom:system_events` for all rooms to avoid complex dynamic subscriptions.
            
            var db = _redisConnection.GetDatabase();
            var streamName = "translationRoom:system_events";
            var groupName = "translationRoom_backend_consumer";

            try
            {
                //"0-0" all events in group
                await db.StreamCreateConsumerGroupAsync(streamName, groupName, "0-0", true);
            }
            catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
            {
                // Idempotent creation: Ignore if the Consumer Group already exists.
                // Other exceptions (e.g., network, auth) will bubble up and crash the app for fail-fast recovery.
            }

            var consumerName = $"backend-{Environment.MachineName}-{Guid.NewGuid().ToString("N")[..8]}";
            _logger.LogInformation("Starting Redis stream consumer with name: {ConsumerName}", consumerName);
            
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var messages = await db.StreamReadGroupAsync(
                        streamName, 
                        groupName, 
                        consumerName, 
                        ">", 
                        count: 10);

                    foreach (var message in messages)
                    {
                        await ProcessMessageWithRetryAsync(message, stoppingToken);
                        await db.StreamAcknowledgeAsync(streamName, groupName, message.Id);
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

    private async Task ProcessMessageWithRetryAsync(StreamEntry message, CancellationToken ct)
    {
        int maxRetries = 3;
        int attempt = 0;
        bool isProcessed = false;
        string lastError = "Unknown error";

        var values = message.Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString());

        var eventTypeStr = values.GetValueOrDefault("event_type", "");
        var routeIdStr = values.GetValueOrDefault("route_id", "");
        var roomIdStr = values.GetValueOrDefault("room_id", "");
        var payloadStr = values.GetValueOrDefault("payload", "");

        while (attempt < maxRetries && !isProcessed)
        {
            attempt++;
            try
            {
                if (Guid.TryParse(roomIdStr, out var roomId))
                {
                    Guid? routeId = Guid.TryParse(routeIdStr, out var parsedRouteId) ? parsedRouteId : null;

                    using var scope = _scopeFactory.CreateScope();
                    var processorService = scope.ServiceProvider.GetRequiredService<IAudioRouteEventProcessorService>();
                    
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

    private async Task RouteToDlqAsync(StreamEntry message, string error, CancellationToken ct)
    {
        try
        {
            var db = _redisConnection.GetDatabase();
            var dlqStream = "translationRoom:system_events:dlq";

            var entryValues = new System.Collections.Generic.List<NameValueEntry>();
            foreach (var value in message.Values)
            {
                entryValues.Add(value);
            }

            entryValues.Add(new NameValueEntry("original_message_id", message.Id.ToString()));
            entryValues.Add(new NameValueEntry("error_message", error));
            entryValues.Add(new NameValueEntry("failed_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

            await db.StreamAddAsync(dlqStream, entryValues.ToArray());
            _logger.LogInformation("Successfully routed message {MessageId} to DLQ stream {DlqStream}", message.Id, dlqStream);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to push message {MessageId} to DLQ stream.", message.Id);
        }
    }
}
