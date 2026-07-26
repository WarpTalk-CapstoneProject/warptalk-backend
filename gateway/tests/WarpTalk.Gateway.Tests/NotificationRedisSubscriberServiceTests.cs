using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using System.Text.Json;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Gateway.Services;
using WarpTalk.Shared.Models;

namespace WarpTalk.Gateway.Tests;

public class NotificationRedisSubscriberServiceTests
{
    private readonly Mock<IConnectionMultiplexer> _mockRedis;
    private readonly Mock<ISubscriber> _mockSubscriber;
    private readonly Mock<IHubContext<NotificationHub>> _mockHubContext;
    private readonly Mock<IHubClients> _mockClients;
    private readonly Mock<IClientProxy> _mockClientProxy;
    private readonly Mock<ILogger<NotificationRedisSubscriberService>> _mockLogger;
    
    private readonly NotificationRedisSubscriberService _service;
    private readonly ConcurrentDictionary<string, Action<RedisChannel, RedisValue>> _messageHandlers = new();

    public NotificationRedisSubscriberServiceTests()
    {
        _mockRedis = new Mock<IConnectionMultiplexer>();
        _mockSubscriber = new Mock<ISubscriber>();
        
        _mockHubContext = new Mock<IHubContext<NotificationHub>>();
        _mockClients = new Mock<IHubClients>();
        _mockClientProxy = new Mock<IClientProxy>();
        _mockLogger = new Mock<ILogger<NotificationRedisSubscriberService>>();

        // Setup Redis
        _mockRedis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(_mockSubscriber.Object);
        
        // Capture the SubscribeAsync callback
        _mockSubscriber.Setup(s => s.SubscribeAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()))
        .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((c, h, f) =>
        {
            _messageHandlers[c.ToString()] = h;
        })
        .Returns(Task.CompletedTask);

        // Setup HubContext
        _mockHubContext.Setup(c => c.Clients).Returns(_mockClients.Object);
        _mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_mockClientProxy.Object);

        _service = new NotificationRedisSubscriberService(
            _mockRedis.Object,
            _mockHubContext.Object,
            _mockLogger.Object);
    }

    [Fact]
    public async Task StartAsync_SubscribesToRedisChannel()
    {
        // Act
        await StartServiceAsync();

        // Assert
        _mockSubscriber.Verify(s => s.SubscribeAsync(
            It.IsAny<RedisChannel>(),
            It.IsAny<Action<RedisChannel, RedisValue>>(),
            It.IsAny<CommandFlags>()), Times.Exactly(5));

        Assert.Contains("warptalk:notifications:new", _messageHandlers.Keys);
        Assert.Contains("warptalk:documents:events", _messageHandlers.Keys);
    }

    [Fact]
    public async Task RedisMessageHandler_WithValidJson_BroadcastsToUserGroup()
    {
        // Arrange
        await StartServiceAsync();
        var message = new RealtimeNotificationMessage
        {
            Id = Guid.NewGuid().ToString(),
            UserId = "user-123",
            Type = "SYSTEM",
            Title = "Test Title",
            Content = "Test Content"
        };
        var json = JsonSerializer.Serialize(message);

        // Act
        _messageHandlers["warptalk:notifications:new"](
            RedisChannel.Literal("warptalk:notifications:new"),
            new RedisValue(json));

        // Assert
        _mockClients.Verify(c => c.Group("user:user-123"), Times.Once);
        _mockClientProxy.Verify(p => p.SendCoreAsync(
            "NewNotification",
            It.Is<object[]>(args => 
                args.Length > 0 && 
                args[0] is RealtimeNotificationMessage &&
                ((RealtimeNotificationMessage)args[0]).UserId == "user-123"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RedisMessageHandler_WithEmptyMessage_DoesNotBroadcast()
    {
        // Arrange
        await StartServiceAsync();

        // Act
        _messageHandlers["warptalk:notifications:new"](
            RedisChannel.Literal("warptalk:notifications:new"),
            RedisValue.EmptyString);

        // Assert
        _mockClients.Verify(c => c.Group(It.IsAny<string>()), Times.Never);
        _mockClientProxy.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RedisMessageHandler_WithInvalidJson_DoesNotCrashAndDoesNotBroadcast()
    {
        // Arrange
        await StartServiceAsync();
        var invalidJson = "{ invalid_json: ";

        // Act - should catch exception inside the handler
        _messageHandlers["warptalk:notifications:new"](
            RedisChannel.Literal("warptalk:notifications:new"),
            new RedisValue(invalidJson));

        // Assert
        _mockClients.Verify(c => c.Group(It.IsAny<string>()), Times.Never);
    }

    private async Task StartServiceAsync()
    {
        await _service.StartAsync(CancellationToken.None);
        for (var attempt = 0; attempt < 100 && _messageHandlers.Count < 5; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(5, _messageHandlers.Count);
    }
}
