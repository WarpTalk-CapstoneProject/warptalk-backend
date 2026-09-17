using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using WarpTalk.NotificationService.Application.DTOs.AdminNotifications;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Application.Mappers;
using WarpTalk.NotificationService.Domain.Constants;

namespace WarpTalk.NotificationService.API.HostedServices;

public class NotificationStreamConsumerService : BackgroundService
{
    private const string StreamName = "admin-notifications-delivery";
    private const string DeadLetterStreamName = "admin-notifications-delivery:dead-letter";
    private const string ConsumerGroupName = "notification-worker-group";
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
        if (!await EnsureConsumerGroupAsync(db, stoppingToken))
            return;

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

    /// <summary>
    /// GUARDED: only BUSYGROUP used to be caught here, so an unreachable Redis threw XGROUP
    /// out of <see cref="ExecuteAsync"/> and tripped BackgroundServiceExceptionBehavior.StopHost,
    /// killing NotificationService rather than just this worker. Retries with bounded backoff so
    /// delivery resumes on its own once Redis returns.
    /// </summary>
    /// <returns>true once the group exists; false only when the host is shutting down.</returns>
    private async Task<bool> EnsureConsumerGroupAsync(IDatabase db, CancellationToken ct)
    {
        var retryDelay = TimeSpan.FromSeconds(2);
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await db.StreamCreateConsumerGroupAsync(
                    StreamName,
                    ConsumerGroupName,
                    "0-0",
                    createStream: true);
                return true;
            }
            catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP", StringComparison.Ordinal))
            {
                // The group is shared by all replicas and is expected to exist after the first start.
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
                    "Admin notification delivery worker could not create consumer group {Group} on {Stream} "
                    + "(attempt {Attempt}); retrying in {RetryDelay}. Admin notifications are NOT being "
                    + "delivered until it succeeds.",
                    ConsumerGroupName, StreamName, attempt, retryDelay);

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
        using var scope = _scopeFactory.CreateScope();
        var deliveryService = scope.ServiceProvider.GetRequiredService<IAdminNotificationDeliveryService>();
        var messages = await deliveryService.DeliverChunkAsync(
            payload,
            StableEventId(logicalEventId),
            cancellationToken);

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
            await MarkAnnouncementFailedAsync(payload, logicalEventId);
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

    /// <summary>
    /// Before this, a dead-lettered chunk left its announcement "Pending" forever, identical in the
    /// admin list to one still on its way. Best-effort: the event is already safe in the DLQ, so a
    /// payload that cannot be read or a database that cannot be reached is logged, not rethrown —
    /// rethrowing here would skip the acknowledgement below and re-dead-letter the same event.
    /// </summary>
    private async Task MarkAnnouncementFailedAsync(string? payload, string logicalEventId)
    {
        Guid notificationId;
        try
        {
            notificationId = string.IsNullOrWhiteSpace(payload)
                ? Guid.Empty
                : JsonSerializer.Deserialize<DeliveryEventPayload>(payload)?.NotificationId ?? Guid.Empty;
        }
        catch (JsonException)
        {
            notificationId = Guid.Empty;
        }

        if (notificationId == Guid.Empty)
        {
            _logger.LogError(
                "Dead-lettered admin notification delivery {EventId} names no announcement; its status cannot be updated.",
                logicalEventId);
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            await scope.ServiceProvider
                .GetRequiredService<IAdminNotificationDeliveryService>()
                .MarkDeliveryFailedAsync(notificationId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Could not mark admin notification {NotificationId} Failed after delivery {EventId} was dead-lettered.",
                notificationId,
                logicalEventId);
        }
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
