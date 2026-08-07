using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using System.Text.Json;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Gateway.Services;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// The Gateway relay for room events TranslationRoomService raises over REST. The hub lives in
/// this process and the service does not, so every one of these events reaches clients only if
/// this subscriber turns the Redis command into a SignalR broadcast.
/// </summary>
public class TranslationRoomRedisSubscriberServiceTests
{
    private const string Channel = "warptalk:translation-room:commands";

    private readonly Mock<ISubscriber> _subscriber = new();
    private readonly Mock<IHubClients> _clients = new();
    private readonly Mock<IClientProxy> _proxy = new();
    private readonly TranslationRoomRedisSubscriberService _service;

    private Func<RedisChannel, RedisValue, Task>? _handler;

    public TranslationRoomRedisSubscriberServiceTests()
    {
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(_subscriber.Object);

        _subscriber
            .Setup(s => s.SubscribeAsync(
                It.IsAny<RedisChannel>(),
                It.IsAny<Action<RedisChannel, RedisValue>>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, h, _) =>
                _handler = (c, v) =>
                {
                    h(c, v);
                    return Task.CompletedTask;
                })
            .Returns(Task.CompletedTask);

        var hubContext = new Mock<IHubContext<TranslationRoomHub>>();
        hubContext.Setup(c => c.Clients).Returns(_clients.Object);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_proxy.Object);

        _service = new TranslationRoomRedisSubscriberService(
            redis.Object,
            hubContext.Object,
            new Mock<ILogger<TranslationRoomRedisSubscriberService>>().Object);
    }

    /// <summary>
    /// WT-322. The meeting page has always registered a "TranslationRoomStarted" handler and
    /// nothing ever sent it, so a participant already in the room when the host pressed Start
    /// never learned the room went live — and the flag that gate sets unsubscribes every
    /// interpreter track and drops every transcript segment, leaving them on the untranslated raw
    /// microphones with no captions, indefinitely.
    /// </summary>
    [Fact]
    public async Task RoomStarted_BroadcastsTranslationRoomStartedToTheRoomGroup()
    {
        var roomId = Guid.NewGuid();
        var handler = await SubscribeAsync();

        await handler(
            RedisChannel.Literal(Channel),
            RoomStartedCommand(roomId, participants: Array.Empty<object>()));

        await WaitForGroupAsync($"translationRoom:{roomId}");

        _proxy.Verify(
            p => p.SendCoreAsync(
                "TranslationRoomStarted",
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The client types the argument as TranslationRoomStateDto and feeds it into a store that
    /// does <c>participants: state.participants</c>. The relay must forward the service's
    /// pre-serialized camelCase state untouched rather than re-shaping or dropping it.
    /// </summary>
    [Fact]
    public async Task RoomStarted_ForwardsTheRoomStatePayloadUntouched()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var handler = await SubscribeAsync();

        await handler(
            RedisChannel.Literal(Channel),
            RoomStartedCommand(
                roomId,
                participants: new object[]
                {
                    new
                    {
                        userId = userId.ToString(),
                        displayName = "Already in the room",
                        speakLanguage = "vi",
                        listenLanguage = "en"
                    }
                }));

        await WaitForGroupAsync($"translationRoom:{roomId}");

        var sent = _proxy.Invocations
            .Where(i => i.Method.Name == nameof(IClientProxy.SendCoreAsync)
                        && (string)i.Arguments[0] == "TranslationRoomStarted")
            .Select(i => (object[])i.Arguments[1])
            .Single();

        var state = Assert.IsType<JsonElement>(Assert.Single(sent));
        Assert.Equal(roomId.ToString(), state.GetProperty("translationRoomId").GetString());
        Assert.Equal("ABC-DEF-GHI", state.GetProperty("translationRoomCode").GetString());
        Assert.Equal("IN_PROGRESS", state.GetProperty("status").GetString());

        var participants = state.GetProperty("participants");
        Assert.Equal(1, participants.GetArrayLength());
        Assert.Equal(userId.ToString(), participants[0].GetProperty("userId").GetString());
    }

    [Fact]
    public async Task RoomStarted_WithoutARoomId_BroadcastsNothing()
    {
        var handler = await SubscribeAsync();

        await handler(
            RedisChannel.Literal(Channel),
            new RedisValue(JsonSerializer.Serialize(new { Command = "RoomStarted", RoomId = "" })));

        _clients.Verify(c => c.Group(It.IsAny<string>()), Times.Never);
    }

    /// <summary>Guards the sibling event this one was modelled on (WT-191).</summary>
    [Fact]
    public async Task RoomEnded_StillBroadcastsTranslationRoomEnded()
    {
        var roomId = Guid.NewGuid();
        var handler = await SubscribeAsync();

        await handler(
            RedisChannel.Literal(Channel),
            new RedisValue(JsonSerializer.Serialize(new { Command = "RoomEnded", RoomId = roomId.ToString() })));

        await WaitForGroupAsync($"translationRoom:{roomId}");

        _proxy.Verify(
            p => p.SendCoreAsync(
                "TranslationRoomEnded",
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The waiting-room counterpart. Approve in the People panel is a REST call, so
    /// TranslationRoomHub.AdmitWaitingParticipant never runs and nothing else emitted
    /// "ParticipantAdmitted" — the admitted guest's client had no way to learn it had been let in
    /// (its participant poll is disabled in the lobby, its room query has no refetch interval) and
    /// sat on the waiting spinner until the guest pressed Refresh Status.
    /// </summary>
    [Fact]
    public async Task ParticipantAdmitted_BroadcastsToTheRoomGroupWithTheAdmittedUserId()
    {
        var roomId = Guid.NewGuid();
        var admittedUserId = Guid.NewGuid();
        var handler = await SubscribeAsync();

        await handler(
            RedisChannel.Literal(Channel),
            new RedisValue(JsonSerializer.Serialize(new
            {
                Command = "ParticipantAdmitted",
                RoomId = roomId.ToString(),
                UserId = admittedUserId.ToString()
            })));

        await WaitForGroupAsync($"translationRoom:{roomId}");

        var sent = _proxy.Invocations
            .Where(i => i.Method.Name == nameof(IClientProxy.SendCoreAsync)
                        && (string)i.Arguments[0] == "ParticipantAdmitted")
            .Select(i => (object[])i.Arguments[1])
            .Single();

        // The client compares this against its own user id to decide whether to re-join, so a
        // broadcast that dropped or reshaped it would either release nobody or release everybody.
        Assert.Equal(admittedUserId.ToString(), Assert.Single(sent));
    }

    [Fact]
    public async Task ParticipantAdmitted_WithoutAUserId_BroadcastsNothing()
    {
        var handler = await SubscribeAsync();

        await handler(
            RedisChannel.Literal(Channel),
            new RedisValue(JsonSerializer.Serialize(new
            {
                Command = "ParticipantAdmitted",
                RoomId = Guid.NewGuid().ToString(),
                UserId = ""
            })));

        _clients.Verify(c => c.Group(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// The exact envelope TranslationRoomService.PublishRoomStartedAsync writes to the relay
    /// channel: PascalCase command fields, and a camelCase <c>State</c> the client reads as
    /// TranslationRoomStateDto.
    /// </summary>
    private static RedisValue RoomStartedCommand(Guid roomId, object[] participants) =>
        new(JsonSerializer.Serialize(new
        {
            Command = "RoomStarted",
            RoomId = roomId.ToString(),
            State = new
            {
                translationRoomId = roomId.ToString(),
                translationRoomCode = "ABC-DEF-GHI",
                status = "IN_PROGRESS",
                participants
            }
        }));

    private async Task<Func<RedisChannel, RedisValue, Task>> SubscribeAsync()
    {
        await _service.StartAsync(CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (_handler is null)
        {
            if (timeout.IsCancellationRequested)
                throw new TimeoutException("The subscriber never registered a Redis handler.");
            await Task.Delay(10, timeout.Token);
        }

        return _handler;
    }

    private async Task WaitForGroupAsync(string groupName)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (_clients.Invocations.Any(i =>
                    i.Method.Name == nameof(IHubClients.Group) &&
                    Equals(i.Arguments[0], groupName)))
                return;
            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException($"No broadcast to group {groupName} was observed.");
    }
}
