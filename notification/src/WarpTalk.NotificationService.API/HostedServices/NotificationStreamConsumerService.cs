using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using WarpTalk.NotificationService.Application.DTOs.AdminNotifications;
using WarpTalk.NotificationService.Application.Mappers;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;

namespace WarpTalk.NotificationService.API.HostedServices;

public class NotificationStreamConsumerService : BackgroundService
{
    private const string StreamName = "admin-notifications-delivery";
    private const string DeadLetterStreamName = "admin-notifications-delivery:dead-letter";
    private const string ConsumerGroupName = "notification-worker-group";
    private const string InboxConsumerName = "admin-notification-delivery@v1";
    private const int MaxAttempts = 5;
    private const long ReclaimIdleMilliseconds = 60_000;

    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotificationStreamConsumerService> _logger;
    private readonly string _consumerName =
        $"notification-{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid():N}";

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
            await db.StreamCreateConsumerGroupAsync(
                StreamName,
                ConsumerGroupName,
                "0-0",
                createStream: true);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP", StringComparison.Ordinal))
        {
            // The group is shared by all replicas and is expected to exist after the first start.
        }

        _logger.LogInformation(
            "Admin notification delivery worker started as {ConsumerName}.",
            _consumerName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reclaimed = await db.StreamAutoClaimAsync(
                    StreamName,
                    ConsumerGroupName,
                    _consumerName,
                    ReclaimIdleMilliseconds,
                    "0-0",
                    count: 10);
                var messages = reclaimed.ClaimedEntries;

                if (messages.Length == 0)
                {
                    messages = await db.StreamReadGroupAsync(
                        StreamName,
                        ConsumerGroupName,
                        _consumerName,
                        position: ">",
                        count: 10);
                }

                if (messages.Length == 0)
                {
                    await Task.Delay(1_000, stoppingToken);
                    continue;
                }

                foreach (var message in messages)
                {
                    await HandleMessageAsync(message, db, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Admin notification stream poll failed.");
                await Task.Delay(5_000, stoppingToken);
            }
        }
    }

    private async Task HandleMessageAsync(
        StreamEntry message,
        IDatabase db,
        CancellationToken cancellationToken)
    {
        var payloadValue = GetField(message, "payload");
        var logicalEventId = GetField(message, "event_id") ?? message.Id.ToString();
        var attempt = int.TryParse(GetField(message, "attempt"), out var parsedAttempt)
            ? parsedAttempt
            : 0;

        try
        {
            if (string.IsNullOrWhiteSpace(payloadValue))
                throw new InvalidDataException("Delivery event payload is missing.");

            var payload = JsonSerializer.Deserialize<DeliveryEventPayload>(payloadValue)
                ?? throw new InvalidDataException("Delivery event payload is invalid.");

            await ProcessChunkAsync(payload, logicalEventId, db, cancellationToken);
            await db.StreamAcknowledgeAsync(StreamName, ConsumerGroupName, message.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RetryOrDeadLetterAsync(
                message,
                payloadValue,
                logicalEventId,
                attempt,
                ex,
                db);
        }
    }

    private async Task ProcessChunkAsync(
        DeliveryEventPayload payload,
        string logicalEventId,
        IDatabase db,
        CancellationToken cancellationToken)
    {
        if (payload.TargetAudienceMode != NotificationConstants.TargetModeSpecificUsers
            || payload.SpecificUserIds is not { Length: > 0 })
        {
            throw new InvalidOperationException(
                $"Unsupported admin notification audience mode '{payload.TargetAudienceMode}'.");
        }

        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var inboxRepository = unitOfWork.NotificationInboxMessageRepository;
        var eventId = StableEventId(logicalEventId);
        if (await inboxRepository.HasProcessedAsync(eventId, InboxConsumerName, cancellationToken))
            return;

        var adminNotification = await unitOfWork.AdminNotificationRepository
            .GetByIdAsync(payload.NotificationId, cancellationToken)
            ?? throw new KeyNotFoundException(
                $"Admin notification {payload.NotificationId} does not exist.");

        var targetUserIds = payload.SpecificUserIds.Distinct().ToArray();
        var messages = targetUserIds
            .Select(userId => NotificationMessageMapper.ToEntity(adminNotification, userId))
            .ToArray();

        await unitOfWork.NotificationMessageRepository.AddRangeAsync(messages);
        await inboxRepository.AddAsync(new NotificationInboxMessage
        {
            EventId = eventId,
            Consumer = InboxConsumerName,
            EventType = "admin.notification.delivery@v1",
            ProcessedAt = DateTime.UtcNow
        });
        await unitOfWork.SaveChangesAsync();

        foreach (var notification in messages)
        {
            var realtimeMessage = NotificationMessageMapper.ToRealtimeDto(notification);
            await db.PublishAsync(
                RedisChannel.Literal(NotificationConstants.RedisNewNotificationChannel),
                JsonSerializer.Serialize(realtimeMessage));
        }
    }

    private async Task RetryOrDeadLetterAsync(
        StreamEntry source,
        string? payload,
        string logicalEventId,
        int attempt,
        Exception exception,
        IDatabase db)
    {
        var nextAttempt = attempt + 1;
        if (nextAttempt >= MaxAttempts)
        {
            await db.StreamAddAsync(
                DeadLetterStreamName,
                [
                    new NameValueEntry("payload", payload ?? string.Empty),
                    new NameValueEntry("event_id", logicalEventId),
                    new NameValueEntry("source_id", source.Id),
                    new NameValueEntry("attempt", nextAttempt),
                    new NameValueEntry("error", exception.Message),
                    new NameValueEntry("failed_at", DateTime.UtcNow.ToString("O"))
                ]);
            _logger.LogError(
                exception,
                "Admin notification delivery {EventId} moved to DLQ after {Attempts} attempts.",
                logicalEventId,
                nextAttempt);
        }
        else
        {
            await db.StreamAddAsync(
                StreamName,
                [
                    new NameValueEntry("payload", payload ?? string.Empty),
                    new NameValueEntry("event_id", logicalEventId),
                    new NameValueEntry("attempt", nextAttempt)
                ]);
            _logger.LogWarning(
                exception,
                "Admin notification delivery {EventId} scheduled for retry {Attempt}.",
                logicalEventId,
                nextAttempt);
        }

        await db.StreamAcknowledgeAsync(StreamName, ConsumerGroupName, source.Id);
    }

    private static string? GetField(StreamEntry entry, string name)
    {
        var value = entry.Values.FirstOrDefault(item => item.Name == name).Value;
        return value.HasValue ? value.ToString() : null;
    }

    private static Guid StableEventId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }
}
