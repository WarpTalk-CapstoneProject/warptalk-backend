using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using WarpTalk.NotificationService.API.HostedServices;
using WarpTalk.NotificationService.Application.DTOs.AdminNotifications;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Domain.Constants;

namespace WarpTalk.NotificationService.Tests.API.HostedServices;

/// <summary>
/// A dead-lettered delivery event used to leave its announcement "Pending" forever — the same
/// word the admin list shows for one still on its way.
/// </summary>
public sealed class NotificationStreamConsumerServiceDeliveryStatusTests
{
    private const string StreamName = "admin-notifications-delivery";

    [Fact]
    public async Task FinalAttemptFailing_DeadLettersTheEventAndMarksTheAnnouncementFailed()
    {
        var notificationId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new DeliveryEventPayload(
            notificationId, NotificationConstants.TargetModeSpecificUsers, [Guid.NewGuid()]));
        var delivery = new Mock<IAdminNotificationDeliveryService>();
        delivery
            .Setup(d => d.DeliverChunkAsync(It.IsAny<DeliveryEventPayload>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("gone"));
        var markedFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        delivery
            .Setup(d => d.MarkDeliveryFailedAsync(notificationId, It.IsAny<CancellationToken>()))
            .Callback(() => markedFailed.TrySetResult())
            .Returns(Task.CompletedTask);

        var db = RedisWithOneEntry(new StreamEntry("1-0", [
            new NameValueEntry("payload", payload),
            new NameValueEntry("event_id", "1-0"),
            new NameValueEntry("attempt", 4)
        ]));
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(db.Object);

        var service = new NotificationStreamConsumerService(
            redis.Object,
            ScopeFactoryReturning(delivery.Object),
            NullLogger<NotificationStreamConsumerService>.Instance);

        await service.StartAsync(CancellationToken.None);
        var finished = await Task.WhenAny(markedFailed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        await service.StopAsync(CancellationToken.None);

        Assert.Same(markedFailed.Task, finished);
        // Asserted on the recorded call rather than one overload: SE.Redis 3 has six StreamAddAsync.
        Assert.Single(db.Invocations, i =>
            i.Method.Name == nameof(IDatabase.StreamAddAsync)
            && (RedisKey)i.Arguments[0] == (RedisKey)(StreamName + ":dead-letter"));
    }

    [Fact]
    public async Task RetryableFailure_DoesNotMarkTheAnnouncementFailed()
    {
        var notificationId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new DeliveryEventPayload(
            notificationId, NotificationConstants.TargetModeSpecificUsers, [Guid.NewGuid()]));
        var delivery = new Mock<IAdminNotificationDeliveryService>();
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        delivery
            .Setup(d => d.DeliverChunkAsync(It.IsAny<DeliveryEventPayload>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback(() => attempted.TrySetResult())
            .ThrowsAsync(new TimeoutException("db busy"));

        var db = RedisWithOneEntry(new StreamEntry("1-0", [
            new NameValueEntry("payload", payload),
            new NameValueEntry("attempt", 0)
        ]));
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(db.Object);

        var service = new NotificationStreamConsumerService(
            redis.Object,
            ScopeFactoryReturning(delivery.Object),
            NullLogger<NotificationStreamConsumerService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.WhenAny(attempted.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        await Task.Delay(100);
        await service.StopAsync(CancellationToken.None);

        Assert.True(attempted.Task.IsCompleted);
        delivery.Verify(d => d.MarkDeliveryFailedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Mock<IDatabase> RedisWithOneEntry(StreamEntry entry)
    {
        var db = new Mock<IDatabase>();
        db.Setup(d => d.StreamCreateConsumerGroupAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(), It.IsAny<bool>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);
        db.Setup(d => d.StreamAutoClaimAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<long>(),
                It.IsAny<RedisValue>(), It.IsAny<int?>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(StreamAutoClaimResult.Null);
        // Hand the entry out once, across whichever of the three key-based overloads the
        // consumer's named-argument call binds to.
        var delivered = 0;
        StreamEntry[] Next() => Interlocked.Exchange(ref delivered, 1) == 0 ? [entry] : [];
        db.Setup(d => d.StreamReadGroupAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(),
                It.IsAny<int?>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(Next);
        db.Setup(d => d.StreamReadGroupAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(),
                It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(Next);
        db.Setup(d => d.StreamReadGroupAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(),
                It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(Next);
        return db;
    }

    private static IServiceScopeFactory ScopeFactoryReturning(IAdminNotificationDeliveryService delivery)
    {
        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IAdminNotificationDeliveryService))).Returns(delivery);
        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        var factory = new Mock<IServiceScopeFactory>();
        factory.Setup(f => f.CreateScope()).Returns(scope.Object);
        return factory.Object;
    }
}
