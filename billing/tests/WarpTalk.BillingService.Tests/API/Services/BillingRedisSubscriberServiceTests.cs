using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using WarpTalk.BillingService.API.Hubs;
using WarpTalk.BillingService.API.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.Shared.Coordination;
using WarpTalk.Shared.Models;

namespace WarpTalk.BillingService.Tests.API.Services;

public class BillingRedisSubscriberServiceTests
{
    private readonly Mock<IConnectionMultiplexer> _redis = new();
    private readonly Mock<ISubscriber> _subscriber = new();
    private readonly Mock<IHubContext<BillingHub>> _hubContext = new();
    private readonly Mock<IHubClients> _clients = new();
    private readonly Mock<IClientProxy> _clientProxy = new();
    private readonly Mock<ILogger<BillingRedisSubscriberService>> _logger = new();
    private readonly Mock<IPubSubLeadership> _leadership = new();
    private readonly BillingRedisSubscriberService _service;
    private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Action<RedisChannel, RedisValue>? _messageHandler;
    private RedisChannel _subscribedChannel;

    public BillingRedisSubscriberServiceTests()
    {
        _redis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(_subscriber.Object);
        _subscriber
            .Setup(s => s.SubscribeAsync(
                It.IsAny<RedisChannel>(),
                It.IsAny<Action<RedisChannel, RedisValue>>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((channel, handler, _) =>
            {
                _subscribedChannel = channel;
                _messageHandler = handler;
                _subscribed.SetResult();
            })
            .Returns(Task.CompletedTask);

        _hubContext.Setup(c => c.Clients).Returns(_clients.Object);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_clientProxy.Object);

        _leadership.SetupGet(l => l.ShouldHandle).Returns(true);
        _service = new BillingRedisSubscriberService(_redis.Object, _hubContext.Object, _logger.Object, _leadership.Object);
    }

    [Fact]
    public async Task StartAsync_SubscribesToBillingNotificationChannel()
    {
        await _service.StartAsync(CancellationToken.None);
        await _subscribed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        _subscriber.Verify(s => s.SubscribeAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()), Times.Once);
        Assert.Equal(RedisChannel.Literal(BillingMessageConstants.Notifications.Channel), _subscribedChannel);
        Assert.NotNull(_messageHandler);
    }

    [Fact]
    public async Task StartAsync_WhenRedisSubscribeFails_DoesNotThrow()
    {
        var redis = new Mock<IConnectionMultiplexer>();
        var subscriber = new Mock<ISubscriber>();
        redis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(subscriber.Object);
        subscriber
            .Setup(s => s.SubscribeAsync(
                It.IsAny<RedisChannel>(),
                It.IsAny<Action<RedisChannel, RedisValue>>(),
                It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis unavailable"));
        var service = new BillingRedisSubscriberService(redis.Object, _hubContext.Object, _logger.Object, _leadership.Object);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        _leadership.Verify(l => l.MarkSubscribed(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_ReportsTheSubscriptionSoThisReplicaMayStandForLeader()
    {
        await _service.StartAsync(CancellationToken.None);
        await _subscribed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        _leadership.Verify(l => l.MarkSubscribed(BillingRedisSubscriberService.SubscriptionKey), Times.Once);
    }

    /// <summary>
    /// Every billing replica receives every pub/sub message; with the SignalR backplane one group
    /// send already reaches all replicas' connections, so a follower forwarding too would deliver
    /// the notification once per replica.
    /// </summary>
    [Fact]
    public async Task RedisMessageHandler_OnAFollowerReplica_DoesNotBroadcast()
    {
        _leadership.SetupGet(l => l.ShouldHandle).Returns(false);
        await _service.StartAsync(CancellationToken.None);
        await _subscribed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await PublishMessageAsync(JsonSerializer.Serialize(new RealtimeNotificationMessage
        {
            Id = Guid.NewGuid().ToString(),
            UserId = "user-123",
            Type = BillingMessageConstants.Notifications.TypePrefix + "credits.updated",
        }));
        await Task.Delay(50);

        _clientProxy.Verify(p => p.SendCoreAsync(
            It.IsAny<string>(),
            It.IsAny<object[]>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RedisMessageHandler_WithBillingNotification_BroadcastsToUserBillingGroup()
    {
        await _service.StartAsync(CancellationToken.None);
        await _subscribed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var broadcasted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _clientProxy
            .Setup(p => p.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => broadcasted.SetResult())
            .Returns(Task.CompletedTask);

        var message = new RealtimeNotificationMessage
        {
            Id = Guid.NewGuid().ToString(),
            UserId = "user-123",
            Type = BillingMessageConstants.Notifications.TypePrefix + "credits.updated",
            Title = "Credits updated",
            Content = "Your billing credits changed."
        };

        await PublishMessageAsync(JsonSerializer.Serialize(message));

        await broadcasted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        _clients.Verify(c => c.Group(
            string.Format(BillingMessageConstants.Notifications.HubGroups.UserGroupTemplate, message.UserId)),
            Times.Once);
        _clientProxy.Verify(p => p.SendCoreAsync(
            BillingMessageConstants.Notifications.HubEvents.BillingNotification,
            It.Is<object[]>(args =>
                args.Length == 1 &&
                args[0] is RealtimeNotificationMessage &&
                ((RealtimeNotificationMessage)args[0]).UserId == message.UserId &&
                ((RealtimeNotificationMessage)args[0]).Type == message.Type),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RedisMessageHandler_WithNonBillingNotification_DoesNotBroadcast()
    {
        await _service.StartAsync(CancellationToken.None);
        await _subscribed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var message = new RealtimeNotificationMessage
        {
            Id = Guid.NewGuid().ToString(),
            UserId = "user-123",
            Type = "system.notice",
            Title = "System",
            Content = "Not billing."
        };

        await PublishMessageAsync(JsonSerializer.Serialize(message));

        _clients.Verify(c => c.Group(It.IsAny<string>()), Times.Never);
        _clientProxy.Verify(p => p.SendCoreAsync(
            It.IsAny<string>(),
            It.IsAny<object[]>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RedisMessageHandler_WithEmptyMessage_DoesNotBroadcast()
    {
        await _service.StartAsync(CancellationToken.None);
        await _subscribed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await PublishMessageAsync(RedisValue.EmptyString);

        _clients.Verify(c => c.Group(It.IsAny<string>()), Times.Never);
        _clientProxy.Verify(p => p.SendCoreAsync(
            It.IsAny<string>(),
            It.IsAny<object[]>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RedisMessageHandler_WithInvalidJson_DoesNotBroadcast()
    {
        await _service.StartAsync(CancellationToken.None);
        await _subscribed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await PublishMessageAsync("{ invalid_json: ");

        _clients.Verify(c => c.Group(It.IsAny<string>()), Times.Never);
    }

    private async Task PublishMessageAsync(RedisValue message)
    {
        var channel = RedisChannel.Literal(BillingMessageConstants.Notifications.Channel);
        _messageHandler?.Invoke(channel, message);
        await Task.Yield();
    }
}
