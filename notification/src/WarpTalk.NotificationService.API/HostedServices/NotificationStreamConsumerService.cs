using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WarpTalk.NotificationService.Application.DTOs.AdminNotifications;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.Shared.Models;
using WarpTalk.NotificationService.Application.Mappers;

namespace WarpTalk.NotificationService.API.HostedServices;
//Background worker to connect to redis streams as a consumer group/
public class NotificationStreamConsumerService : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotificationStreamConsumerService> _logger;

    private const string StreamName = "admin-notifications-delivery";
    private const string ConsumerGroupName = "notification-worker-group";
    private const string ConsumerName = "worker-1"; // In a real cluster, generate dynamically e.g. Environment.MachineName

    public NotificationStreamConsumerService(
        IConnectionMultiplexer redis,
        IServiceScopeFactory scopeFactory,
        ILogger<NotificationStreamConsumerService> logger)
    {
        _redis = redis;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = _redis.GetDatabase();

       
        try
        {
            await db.StreamCreateConsumerGroupAsync(StreamName, ConsumerGroupName, "0-0", createStream: true);
            _logger.LogInformation("Created consumer group {ConsumerGroup} for stream {Stream}.", ConsumerGroupName, StreamName);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
        {
            // Group already exists, ignore
        }

        _logger.LogInformation("NotificationStreamConsumerService started processing chunks.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 2. Read new messages for the consumer group. ">" means read undelivered messages.
                var messages = await db.StreamReadGroupAsync(
                    StreamName, 
                    ConsumerGroupName, 
                    ConsumerName, 
                    position: ">", 
                    count: 1); // Process 1 chunk at a time

                if (messages.Length == 0)
                {
                    await Task.Delay(1000, stoppingToken); // Backoff
                    continue;
                }

                foreach (var message in messages)
                {
                    var payloadValue = message.Values.FirstOrDefault(v => v.Name == "payload").Value;
                    if (!payloadValue.HasValue) continue;

                    var payload = JsonSerializer.Deserialize<DeliveryEventPayload>(payloadValue.ToString());
                    if (payload == null) continue;

                    await ProcessChunkAsync(payload, db, stoppingToken);

                    // 3. Acknowledge the message
                    await db.StreamAcknowledgeAsync(StreamName, ConsumerGroupName, message.Id);
                    _logger.LogInformation("Acknowledged message {MessageId}.", message.Id);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing stream message.");
                await Task.Delay(5000, stoppingToken); // Backoff on error
            }
        }
    }

    private async Task ProcessChunkAsync(DeliveryEventPayload payload, IDatabase db, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        // 1. Get the admin notification
        var adminNotif = await unitOfWork.AdminNotificationRepository.GetByIdAsync(payload.NotificationId, ct);
        if (adminNotif == null)
        {
            _logger.LogWarning("AdminNotification {Id} not found. Skipping delivery.", payload.NotificationId);
            return;
        }

        // 2. Resolve users
        Guid[] targetUserIds = Array.Empty<Guid>();

        if (payload.TargetAudienceMode == NotificationConstants.TargetModeSpecificUsers && payload.SpecificUserIds != null)
        {
            targetUserIds = payload.SpecificUserIds;
        }
        else if (payload.TargetAudienceMode == NotificationConstants.TargetModeBroadcast)
        {
            // For broadcast, ideally we query a User microservice to get all IDs.
            // For now, since this is a capstone, we assume the specific user list is passed or we leave it empty.
            // We'll simulate fetching all users (e.g., getting 10 users for testing).
            // This would normally involve pagination.
            _logger.LogWarning("Broadcast mode resolution is not fully implemented. Mocking empty list.");
        }
        else if (payload.TargetAudienceMode == NotificationConstants.TargetModeSegment)
        {
            _logger.LogWarning("Segment mode resolution is not fully implemented. Mocking empty list.");
        }

        if (!targetUserIds.Any()) return;

        // 3. Create NotificationMessage entities
        var messagesToInsert = targetUserIds.Select(userId => NotificationMessageMapper.ToEntity(adminNotif, userId)).ToList();

        // 4. Bulk Insert
        await unitOfWork.NotificationMessageRepository.AddRangeAsync(messagesToInsert);
        await unitOfWork.SaveChangesAsync();
        
        _logger.LogInformation("Saved {Count} notification messages to inbox for Notification {AdminNotifId}.", messagesToInsert.Count, adminNotif.Id);

        // 5. Fan-out via Redis Pub/Sub for Realtime Delivery
        foreach (var msg in messagesToInsert)
        {
            var realtimeMsg = NotificationMessageMapper.ToRealtimeDto(msg);

            await db.PublishAsync(RedisChannel.Literal(NotificationConstants.RedisNewNotificationChannel), JsonSerializer.Serialize(realtimeMsg));
        }
        
        _logger.LogInformation("Published {Count} realtime messages to Gateway.", messagesToInsert.Count);
    }
}
