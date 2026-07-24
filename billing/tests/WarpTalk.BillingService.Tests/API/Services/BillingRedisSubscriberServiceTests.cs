using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using WarpTalk.BillingService.API.Hubs;
using WarpTalk.BillingService.API.Services;
using WarpTalk.BillingService.Domain.Constants;
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
    private readonly BillingRedisSubscriberService _service;

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
            })
            .Returns(Task.CompletedTask);

        _hubContext.Setup(c => c.Clients).Returns(_clients.Object);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_clientProxy.Object);

        _service = new BillingRedisSubscriberService(_redis.Object, _hubContext.Object, _logger.Object);
    }

    [Fact]
    public async Task StartAsync_SubscribesToBillingNotificationChannel()
    {
        await _service.StartAsync(CancellationToken.None);

        _subscriber.Verify(s => s.SubscribeAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()), Times.Once);
        Assert.Equal(RedisChannel.Literal(BillingMessageConstants.Notifications.Channel), _subscribedChannel);
        Assert.NotNull(_messageHandler);
    }

    [Fact]
    public async Task RedisMessageHandler_WithBillingNotification_BroadcastsToUserBillingGroup()
    {
        await _service.StartAsync(CancellationToken.None);
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

        _messageHandler?.Invoke(
            RedisChannel.Literal(BillingMessageConstants.Notifications.Channel),
            new RedisValue(JsonSerializer.Serialize(message)));

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
        var message = new RealtimeNotificationMessage
        {
            Id = Guid.NewGuid().ToString(),
            UserId = "user-123",
            Type = "system.notice",
            Title = "System",
            Content = "Not billing."
        };

        _messageHandler?.Invoke(
            RedisChannel.Literal(BillingMessageConstants.Notifications.Channel),
            new RedisValue(JsonSerializer.Serialize(message)));

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

        _messageHandler?.Invoke(
            RedisChannel.Literal(BillingMessageConstants.Notifications.Channel),
            RedisValue.EmptyString);

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

        _messageHandler?.Invoke(
            RedisChannel.Literal(BillingMessageConstants.Notifications.Channel),
            new RedisValue("{ invalid_json: "));

        _clients.Verify(c => c.Group(It.IsAny<string>()), Times.Never);
    }
}
