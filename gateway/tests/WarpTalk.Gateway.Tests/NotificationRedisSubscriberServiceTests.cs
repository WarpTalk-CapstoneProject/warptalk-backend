using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using System.Text.Json;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Gateway.Services;
using WarpTalk.Shared.Models;
using WarpTalk.Shared.Events;

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
    private readonly Dictionary<string, Action<RedisChannel, RedisValue>> _messageHandlers = new();

    private async Task<Action<RedisChannel, RedisValue>> GetNotificationHandlerAsync()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (_messageHandlers.TryGetValue("warptalk:notifications:new", out var handler))
            {
                return handler;
            }
            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException("Notification Redis subscription was not registered.");
    }

    private async Task<Action<RedisChannel, RedisValue>> GetHandlerAsync(string channel)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (_messageHandlers.TryGetValue(channel, out var handler))
                return handler;
            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException($"Redis subscription {channel} was not registered.");
    }

    private async Task WaitForSubscriptionsAsync(int expectedCount)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (_messageHandlers.Count == expectedCount)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException(
            $"Expected {expectedCount} Redis subscriptions, but found {_messageHandlers.Count}.");
    }

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
        await _service.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(5);

        // Assert
        Assert.Equal(5, _messageHandlers.Count);
        Assert.Contains("warptalk:notifications:new", _messageHandlers.Keys);
    }

    [Fact]
    public async Task MeetingStartedHandler_BroadcastsVersionedPayloadToWorkspace()
    {
        await _service.StartAsync(CancellationToken.None);
        var handler = await GetHandlerAsync("meeting.started");
        var workspaceId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var envelope = DomainEventEnvelope.Create(
            MeetingEventTypes.Started,
            "meeting-service",
            workspaceId.ToString(),
            new MeetingStartedEventPayload(roomId, workspaceId));

        handler(
            RedisChannel.Literal("meeting.started"),
            JsonSerializer.Serialize(envelope));

        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (_mockClients.Invocations.Any(invocation =>
                    invocation.Method.Name == nameof(IHubClients.Group) &&
                    Equals(invocation.Arguments[0], $"workspace:{workspaceId}")))
                break;
            await Task.Delay(10);
        }

        _mockClients.Verify(clients => clients.Group($"workspace:{workspaceId}"), Times.AtLeastOnce);
        _mockClientProxy.Verify(proxy => proxy.SendCoreAsync(
            "MeetingStarted",
            It.Is<object[]>(args => HasMeetingStartedPayload(args, roomId)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RedisMessageHandler_WithValidJson_BroadcastsToUserGroup()
    {
        // Arrange
        await _service.StartAsync(CancellationToken.None);
        var handler = await GetNotificationHandlerAsync();
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
        handler(
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
        await _service.StartAsync(CancellationToken.None);
        var handler = await GetNotificationHandlerAsync();

        // Act
        handler(
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
        await _service.StartAsync(CancellationToken.None);
        var handler = await GetNotificationHandlerAsync();
        var invalidJson = "{ invalid_json: ";

        // Act - should catch exception inside the handler
        handler(
            RedisChannel.Literal("warptalk:notifications:new"),
            new RedisValue(invalidJson));

        // Assert
        _mockClients.Verify(c => c.Group(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task WorkspaceEventHandler_WithUserId_SendsEventTypeToTheUserGroupNotToAGroupNamedAfterTheEvent()
    {
        await _service.StartAsync(CancellationToken.None);
        var handler = await GetHandlerAsync("warptalk:workspace:events");

        handler(
            RedisChannel.Literal("warptalk:workspace:events"),
            new RedisValue("""{"eventType":"MemberRoleUpdated","userId":"user-987"}"""));

        await WaitForGroupAsync("user:user-987");

        // Both the generic "WorkspaceEvent" fan-out and the specific event type must be
        // addressed to the user's own group. Sending the specific one to Group(eventType) —
        // a group nobody joins — silently dropped every user-scoped workspace event.
        _mockClients.Verify(c => c.Group("user:user-987"), Times.Exactly(2));
        _mockClients.Verify(c => c.Group("MemberRoleUpdated"), Times.Never);
        _mockClientProxy.Verify(p => p.SendCoreAsync(
            "MemberRoleUpdated",
            It.IsAny<object[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MeetingEventHandler_RelaysMeetingInvitedToTheWorkspaceGroup()
    {
        // WT-187: TranslationRoomService publishes this when a room's invitation list changes.
        // "MeetingInvited" is intentionally not a name the web client binds directly, so it
        // must still arrive via the generic "MeetingEvent" fan-out — that is what makes the
        // invitee's rooms list refetch.
        await _service.StartAsync(CancellationToken.None);
        var handler = await GetHandlerAsync("warptalk:meetings:events");
        var workspaceId = Guid.NewGuid();

        handler(
            RedisChannel.Literal("warptalk:meetings:events"),
            new RedisValue(
                $$"""{"eventType":"MeetingInvited","workspaceId":"{{workspaceId}}","roomId":"{{Guid.NewGuid()}}"}"""));

        await WaitForGroupAsync($"workspace:{workspaceId}");

        _mockClientProxy.Verify(p => p.SendCoreAsync(
            "MeetingEvent",
            It.IsAny<object[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockClientProxy.Verify(p => p.SendCoreAsync(
            "MeetingInvited",
            It.IsAny<object[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private async Task WaitForGroupAsync(string groupName)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (_mockClients.Invocations.Any(invocation =>
                    invocation.Method.Name == nameof(IHubClients.Group) &&
                    Equals(invocation.Arguments[0], groupName)))
                return;
            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException($"No broadcast to group {groupName} was observed.");
    }

    private static bool HasMeetingStartedPayload(object[] args, Guid roomId)
        => args.Length == 1 &&
           args[0] is MeetingStartedEventPayload payload &&
           payload.TranslationRoomId == roomId;
}
