using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using WarpTalk.Gateway.Constants;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Gateway.Services;
using WarpTalk.Gateway.Tests.Helpers;
using WarpTalk.Shared.Coordination;

namespace WarpTalk.Gateway.Tests.Coordination;

/// <summary>
/// The duplicate-delivery fix: with the SignalR backplane, one Clients.Group send already reaches
/// every gateway pod, so exactly one pod may forward each pub/sub message. These tests run two
/// "pods" (two elector + subscriber pairs over one lease store) and deliver every message to both,
/// exactly as Redis pub/sub does.
/// </summary>
public sealed class LeaderElectedRelayTests
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(5);

    private readonly ManualTimeProvider _time = new();
    private readonly InProcessLeaseStore _store;

    public LeaderElectedRelayTests()
    {
        _store = new InProcessLeaseStore(_time);
    }

    [Fact]
    public async Task OnlyTheLeaderBroadcasts_WhenEveryPodReceivesTheMessage()
    {
        var a = await StartPodAsync();
        var b = await StartPodAsync();
        await a.Elector.TickAsync(CancellationToken.None);
        await b.Elector.TickAsync(CancellationToken.None);

        Assert.True(a.Elector.IsLeader ^ b.Elector.IsLeader);

        await DeliverToAllAsync(RoomEnded(), a, b);

        Assert.Equal(1, a.Sends + b.Sends);
        Assert.Equal(1, (a.Elector.IsLeader ? a : b).Sends);
    }

    [Fact]
    public async Task A_PodIsNotEligible_UntilItsSubscriptionsAreLive()
    {
        var pod = CreatePod();

        await pod.Elector.TickAsync(CancellationToken.None);
        Assert.False(pod.Elector.IsLeader); // nothing subscribed yet

        await pod.StartAsync();
        await pod.Elector.TickAsync(CancellationToken.None);
        Assert.True(pod.Elector.IsLeader);
    }

    [Fact]
    public async Task LeaderCrash_FollowerTakesOverAfterTheLease_AndNeverTwoBroadcastAtOnce()
    {
        var a = await StartPodAsync();
        var b = await StartPodAsync();
        await a.Elector.TickAsync(CancellationToken.None);
        await b.Elector.TickAsync(CancellationToken.None);
        var (leader, follower) = a.Elector.IsLeader ? (a, b) : (b, a);

        // The leader "crashes": it stops renewing. Time passes; the follower keeps contending.
        _time.Advance(LeaseDuration - TimeSpan.FromMilliseconds(500));
        await follower.Elector.TickAsync(CancellationToken.None);
        Assert.False(follower.Elector.IsLeader); // lease not expired in the store yet
        Assert.False(leader.Elector.IsLeader);   // but the old leader has already stood down locally

        // In this window neither broadcasts: the documented failover gap, never a duplicate.
        await DeliverToAllAsync(RoomEnded(), a, b);
        Assert.Equal(0, a.Sends + b.Sends);

        _time.Advance(TimeSpan.FromMilliseconds(600));
        await follower.Elector.TickAsync(CancellationToken.None);
        Assert.True(follower.Elector.IsLeader);

        await DeliverToAllAsync(RoomEnded(), a, b);
        Assert.Equal(1, follower.Sends);
        Assert.Equal(0, leader.Sends);
        Assert.True(follower.Elector.FencingToken > 1);
    }

    [Fact]
    public async Task GracefulShutdown_ReleasesTheLease_SoTheFollowerTakesOverWithoutWaiting()
    {
        var a = await StartPodAsync();
        var b = await StartPodAsync();
        await a.Elector.TickAsync(CancellationToken.None);
        await b.Elector.TickAsync(CancellationToken.None);
        var (leader, follower) = a.Elector.IsLeader ? (a, b) : (b, a);

        await leader.Elector.StopAsync(CancellationToken.None);
        await follower.Elector.TickAsync(CancellationToken.None); // no time has passed

        Assert.True(follower.Elector.IsLeader);
        await DeliverToAllAsync(RoomEnded(), a, b);
        Assert.Equal(1, follower.Sends);
        Assert.Equal(0, leader.Sends);
    }

    [Fact]
    public async Task LeaderThatLosesItsSubscriberConnection_StepsDown()
    {
        var a = await StartPodAsync();
        var b = await StartPodAsync();
        await a.Elector.TickAsync(CancellationToken.None);
        await b.Elector.TickAsync(CancellationToken.None);
        var (leader, follower) = a.Elector.IsLeader ? (a, b) : (b, a);

        leader.SubscriberConnected = false;
        await leader.Elector.TickAsync(CancellationToken.None);
        await follower.Elector.TickAsync(CancellationToken.None);

        Assert.False(leader.Elector.IsLeader);
        Assert.True(follower.Elector.IsLeader);
    }

    [Fact]
    public async Task EveryRelaySubscriber_ReportsExactlyTheSubscriptionsTheElectionWaitsFor()
    {
        // If these drift apart, no gateway pod ever becomes eligible and every realtime event is
        // silently dropped — so pin them to each other.
        var leadership = new TestPubSubLeadership();
        var redis = new Mock<IConnectionMultiplexer>();
        var subscriber = new Mock<ISubscriber>();
        redis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(subscriber.Object);
        subscriber.Setup(s => s.SubscribeAsync(It.IsAny<RedisChannel>(), It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()))
            .Returns(Task.CompletedTask);

        var services = new Microsoft.Extensions.Hosting.BackgroundService[]
        {
            new NotificationRedisSubscriberService(redis.Object, HubContext<NotificationHub>(out _), NullLogger<NotificationRedisSubscriberService>.Instance, leadership),
            new BillingRedisSubscriberService(redis.Object, HubContext<BillingHub>(out _), NullLogger<BillingRedisSubscriberService>.Instance, leadership),
            new TranslationRoomRedisSubscriberService(redis.Object, HubContext<TranslationRoomHub>(out _), NullLogger<TranslationRoomRedisSubscriberService>.Instance, leadership),
        };

        foreach (var service in services)
        {
            await service.StartAsync(CancellationToken.None);
        }

        await WaitForAsync(() => leadership.Subscribed.Count >= RealtimeRelay.RequiredSubscriptions.Count);
        Assert.Equal(
            RealtimeRelay.RequiredSubscriptions.OrderBy(k => k),
            leadership.Subscribed.Distinct().OrderBy(k => k));

        foreach (var service in services)
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // ── harness ─────────────────────────────────────────────────

    private sealed class Pod
    {
        public required LeaderElector Elector { get; init; }
        public required TranslationRoomRedisSubscriberService Subscriber { get; init; }
        public required Func<Action<RedisChannel, RedisValue>?> Handler { get; init; }
        public required Func<int> SendCount { get; init; }
        public bool SubscriberConnected { get; set; } = true;
        public int Sends => SendCount();

        public async Task StartAsync()
        {
            await Subscriber.StartAsync(CancellationToken.None);
            await WaitForAsync(() => Handler() is not null);
        }
    }

    private async Task<Pod> StartPodAsync()
    {
        var pod = CreatePod();
        await pod.StartAsync();
        return pod;
    }

    private Pod CreatePod()
    {
        Action<RedisChannel, RedisValue>? handler = null;
        Pod? pod = null;

        var subscriber = new Mock<ISubscriber>();
        subscriber.Setup(s => s.SubscribeAsync(It.IsAny<RedisChannel>(), It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, h, _) => handler = h)
            .Returns(Task.CompletedTask);
        subscriber.Setup(s => s.IsConnected(It.IsAny<RedisChannel>())).Returns(() => pod!.SubscriberConnected);

        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(subscriber.Object);

        var eligibility = new PubSubSubscriptionEligibility(
            redis.Object,
            [RealtimeRelay.Key(nameof(TranslationRoomRedisSubscriberService), RealtimeConstants.RedisChannels.TranslationRoomCommands)]);
        var elector = new LeaderElector(
            new DistributedLockProvider(_store, _time),
            new LeaderElectionOptions { Resource = RealtimeRelay.LeaseResource, LeaseDuration = LeaseDuration },
            NullLogger<LeaderElector>.Instance,
            eligibility);
        var leadership = new PubSubLeadership(elector, eligibility);

        var hub = HubContext<TranslationRoomHub>(out var proxy);
        var service = new TranslationRoomRedisSubscriberService(
            redis.Object, hub, NullLogger<TranslationRoomRedisSubscriberService>.Instance, leadership);

        pod = new Pod
        {
            Elector = elector,
            Subscriber = service,
            Handler = () => handler,
            SendCount = () => proxy.Invocations.Count(i => i.Method.Name == nameof(IClientProxy.SendCoreAsync)),
        };
        return pod;
    }

    private static IHubContext<THub> HubContext<THub>(out Mock<IClientProxy> proxy) where THub : Hub
    {
        proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        clients.Setup(c => c.All).Returns(proxy.Object);
        var context = new Mock<IHubContext<THub>>();
        context.Setup(c => c.Clients).Returns(clients.Object);
        return context.Object;
    }

    private static RedisValue RoomEnded()
        => JsonSerializer.Serialize(new { Command = "RoomEnded", RoomId = Guid.NewGuid().ToString() });

    private static async Task DeliverToAllAsync(RedisValue message, params Pod[] pods)
    {
        var channel = RedisChannel.Literal(RealtimeConstants.RedisChannels.TranslationRoomCommands);
        foreach (var pod in pods)
        {
            pod.Handler()!(channel, message);
        }

        // The handlers are async void lambdas; give them a moment to reach SendAsync.
        await Task.Delay(50);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
