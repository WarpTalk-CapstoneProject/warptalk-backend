using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;
using System.Text.Json;
using WarpTalk.Gateway.Hubs;

namespace WarpTalk.Gateway.Services;

/// <summary>
/// Background service acting as a Redis Pub/Sub subscriber.
/// Listens for new commands from TranslationRoomService (e.g. Kick, CancelRoom) 
/// and broadcasts them in real-time to the appropriate user's SignalR group.
/// </summary>
public class TranslationRoomRedisSubscriberService : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IHubContext<TranslationRoomHub> _hubContext;
    private readonly ILogger<TranslationRoomRedisSubscriberService> _logger;

    public TranslationRoomRedisSubscriberService(
        IConnectionMultiplexer redis,
        IHubContext<TranslationRoomHub> hubContext,
        ILogger<TranslationRoomRedisSubscriberService> logger)
    {
        _redis = redis;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = _redis.GetSubscriber();
        await subscriber.SubscribeAsync(RedisChannel.Literal("warptalk:translation-room:commands"), async (channel, message) =>
        {
            try
            {
                if (message.IsNullOrEmpty) return;

                var payload = JsonSerializer.Deserialize<TranslationRoomCommandMessage>(message.ToString());
                if (payload == null || string.IsNullOrEmpty(payload.Command)) return;

                if (payload.Command == "CancelRoom" && !string.IsNullOrEmpty(payload.RoomId))
                {
                    var groupName = $"translationRoom:{payload.RoomId}";
                    await _hubContext.Clients.Group(groupName).SendAsync("ForceDisconnected", "This room has been cancelled.", stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted ForceDisconnected to room {RoomId}", payload.RoomId);
                }
                else if (payload.Command == "Kick" && !string.IsNullOrEmpty(payload.UserId))
                {
                    // Assuming ConnectionManager tracks users and we can broadcast to the user's specific connection.
                    // But we don't have user's connection ID here. Instead we can broadcast to all in the room,
                    // or broadcast to a global user group if we use UserId as group name.
                    // Here we broadcast to the room, and the client with matching UserId will disconnect.
                    var groupName = $"translationRoom:{payload.RoomId}";
                    await _hubContext.Clients.Group(groupName).SendAsync("ParticipantKicked", payload.UserId, stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted ParticipantKicked to room {RoomId} for user {UserId}", payload.RoomId, payload.UserId);
                }
                // WT-04/WT-06/WT-08: MeetingService (a separate microservice/process from this
                // Gateway) publishes these on the same channel — it cannot inject
                // IHubContext<TranslationRoomHub> directly since it doesn't own this hub's
                // process; this Redis Pub/Sub relay is the established cross-process mechanism
                // (see MeetingRoomService.PublishGatewayCommandAsync).
                else if (payload.Command == "RoomLockChanged" && !string.IsNullOrEmpty(payload.RoomId))
                {
                    var groupName = $"translationRoom:{payload.RoomId}";
                    await _hubContext.Clients.Group(groupName).SendAsync("RoomLockChanged", payload.Locked ?? false, stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted RoomLockChanged({Locked}) to room {RoomId}", payload.Locked, payload.RoomId);
                }
                else if (payload.Command == "RecordingStateChanged" && !string.IsNullOrEmpty(payload.RoomId))
                {
                    var groupName = $"translationRoom:{payload.RoomId}";
                    await _hubContext.Clients.Group(groupName).SendAsync("RecordingStateChanged", payload.Recording ?? false, stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted RecordingStateChanged({Recording}) to room {RoomId}", payload.Recording, payload.RoomId);
                }
                else if (payload.Command == "HostChanged" && !string.IsNullOrEmpty(payload.RoomId) && !string.IsNullOrEmpty(payload.NewHostUserId))
                {
                    var groupName = $"translationRoom:{payload.RoomId}";
                    await _hubContext.Clients.Group(groupName).SendAsync("HostChanged", payload.NewHostUserId, stoppingToken);
                    _logger.LogDebug("RedisSubscriber: Broadcasted HostChanged({NewHostUserId}) to room {RoomId}", payload.NewHostUserId, payload.RoomId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process incoming Redis translation-room command message.");
            }
        });

        _logger.LogInformation("TranslationRoomRedisSubscriberService started listening to 'warptalk:translation-room:commands'.");
    }
}

public class TranslationRoomCommandMessage
{
    public string Command { get; set; } = string.Empty;
    public string RoomId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;

    // WT-04
    public bool? Locked { get; set; }

    // WT-06
    public bool? Recording { get; set; }

    // WT-08
    public string? NewHostUserId { get; set; }
}
