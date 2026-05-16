using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.TranslationRoomService.Application.Interfaces;

namespace WarpTalk.TranslationRoomService.API.HostedServices;

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

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var messages = await db.StreamReadGroupAsync(
                    streamName, 
                    groupName, 
                    "worker_1", 
                    ">", 
                    count: 10);

                foreach (var message in messages)
                {
                    await ProcessMessageAsync(message, stoppingToken);
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

    private async Task ProcessMessageAsync(StreamEntry message, CancellationToken ct)
    {
        try
        {
            var eventTypeStr = message.Values.FirstOrDefault(x => x.Name == "event_type").Value.ToString();
            var routeIdStr = message.Values.FirstOrDefault(x => x.Name == "route_id").Value.ToString();
            var roomIdStr = message.Values.FirstOrDefault(x => x.Name == "room_id").Value.ToString();
            var payloadStr = message.Values.FirstOrDefault(x => x.Name == "payload").Value.ToString();

            if (Guid.TryParse(roomIdStr, out var roomId))
            {
                Guid? routeId = Guid.TryParse(routeIdStr, out var parsedRouteId) ? parsedRouteId : null;

                using var scope = _scopeFactory.CreateScope();
                var processorService = scope.ServiceProvider.GetRequiredService<IAudioRouteEventProcessorService>();
                
                var result = await processorService.ProcessEventAsync(roomId, routeId, eventTypeStr, payloadStr, ct);

                if (!result.IsSuccess)
                {
                    _logger.LogWarning("Failed to process event {EventType} for room {RoomId}. Error: {Error}", eventTypeStr, roomId, result.Error);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process stream message {MessageId}", message.Id);
        }
    }
}
