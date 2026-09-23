using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using Testcontainers.Redis;
using WarpTalk.Gateway.Constants;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Gateway.Services;
using WarpTalk.Gateway.Tests.Helpers;
using WarpTalk.Shared.Coordination;

namespace WarpTalk.Gateway.Tests.Coordination;

/// <summary>
/// The lease store and the leader-elected relay against a real Redis: the Lua compare-and-set
/// scripts, real PX expiry, the release notice over pub/sub, and a real PUBLISH reaching several
/// subscribed "pods" of which only the leader may forward.
/// </summary>
public sealed class RedisCoordinationIntegrationTests : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder().WithImage("redis:7-alpine").Build();
    private readonly List<IConnectionMultiplexer> _connections = [];
    private bool _started;

    public async Task InitializeAsync()
    {
        try
        {
            await _redis.StartAsync();
            _started = true;
        }
        catch
        {
            // Without Docker every test here is skipped by DockerFact before it runs.
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }

        if (_started)
        {
            await _redis.DisposeAsync();
        }
    }

    [DockerFact]
    public async Task Lease_AcquireRenewExpireTakeover_AgainstRealRedis()
    {
        var locks = new DistributedLockProvider(new RedisLeaseStore(await ConnectAsync()), TimeProvider.System);
        var lease = TimeSpan.FromMilliseconds(400);

        var first = await locks.TryAcquireAsync("it:job", lease);
        Assert.NotNull(first);
        Assert.Null(await locks.TryAcquireAsync("it:job", lease));

        await Task.Delay(250);
        Assert.True(await first!.TryRenewAsync());
        await Task.Delay(250); // 500ms after acquire, 250ms after renewal: still held in Redis
        Assert.Null(await locks.TryAcquireAsync("it:job", lease));

        await Task.Delay(300); // no renewal: PX expires
        var second = await locks.TryAcquireAsync("it:job", lease);
        Assert.NotNull(second);
        Assert.True(second!.FencingToken > first.FencingToken);

        Assert.False(await first.TryRenewAsync());
        Assert.False(await first.ReleaseAsync()); // token-safe: does not delete the new lease
        Assert.Null(await locks.TryAcquireAsync("it:job", lease));

        Assert.True(await second.ReleaseAsync());
        Assert.NotNull(await locks.TryAcquireAsync("it:job", lease));
    }

    [DockerFact]
    public async Task Release_OnlyDeletesTheCallersOwnLease()
    {
        var redis = await ConnectAsync();
        var store = new RedisLeaseStore(redis);

        Assert.NotNull(await store.TryAcquireAsync("it:release", "token-a", TimeSpan.FromSeconds(10)));
        Assert.False(await store.ReleaseAsync("it:release", "token-b"));
        Assert.Equal("token-a", (string?)await redis.GetDatabase().StringGetAsync(RedisLeaseStore.LeaseKey("it:release")));
        Assert.True(await store.ReleaseAsync("it:release", "token-a"));
        Assert.False(await redis.GetDatabase().KeyExistsAsync(RedisLeaseStore.LeaseKey("it:release")));
    }

    [DockerFact]
    public async Task Exclusive_ConcurrentReplicas_RunTheTickOnce()
    {
        var runs = 0;
        var replicas = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
            new DistributedLockProvider(new RedisLeaseStore(await ConnectAsync()), TimeProvider.System)));

        var outcomes = await Task.WhenAll(replicas.Select(locks => locks.TryRunExclusiveAsync(
            "it:tick",
            TimeSpan.FromSeconds(5),
            async ct =>
            {
                Interlocked.Increment(ref runs);
                await Task.Delay(300, ct);
            },
            NullLogger.Instance,
            CancellationToken.None)));

        Assert.Equal(1, runs);
        Assert.Equal(1, outcomes.Count(o => o == ExclusiveTickOutcome.Ran));
        Assert.Equal(3, outcomes.Count(o => o == ExclusiveTickOutcome.Skipped));
    }

    [DockerFact]
    public async Task Relay_ThreeGatewayPods_ForwardEachPublishOnce_AndFailOverOnShutdownAndCrash()
    {
        var options = new LeaderElectionOptions
        {
            Resource = "it:" + RealtimeRelay.LeaseResource,
            LeaseDuration = TimeSpan.FromMilliseconds(1500),
            RenewInterval = TimeSpan.FromMilliseconds(300),
            AcquireRetryInterval = TimeSpan.FromMilliseconds(200),
        };

        var pods = new List<RelayPod>();
        for (var i = 0; i < 3; i++)
        {
            pods.Add(await StartRelayPodAsync(options));
        }

        await WaitForAsync(() => pods.Count(p => p.Elector.IsLeader) == 1);
        var publisher = await ConnectAsync();

        await PublishRoomEndedAsync(publisher);
        await WaitForAsync(() => pods.Sum(p => p.Sends) >= 1);
        await Task.Delay(200);
        Assert.Equal(1, pods.Sum(p => p.Sends));

        // Graceful shutdown of the leader: the release notice hands over without a lease timeout.
        var first = pods.Single(p => p.Elector.IsLeader);
        await first.StopAsync();
        pods.Remove(first);
        await WaitForAsync(() => pods.Count(p => p.Elector.IsLeader) == 1, TimeSpan.FromMilliseconds(1000));

        var before = pods.Sum(p => p.Sends);
        await PublishRoomEndedAsync(publisher);
        await WaitForAsync(() => pods.Sum(p => p.Sends) > before);
        await Task.Delay(200);
        Assert.Equal(before + 1, pods.Sum(p => p.Sends));

        // Crash of the leader: it stops renewing and never releases. The survivor waits out the lease.
        var second = pods.Single(p => p.Elector.IsLeader);
        await second.CrashAsync();
        pods.Remove(second);
        await WaitForAsync(() => pods.Single().Elector.IsLeader, TimeSpan.FromSeconds(5));

        before = pods.Single().Sends;
        await PublishRoomEndedAsync(publisher);
        await WaitForAsync(() => pods.Single().Sends > before);

        foreach (var pod in pods.Append(second))
        {
            await pod.StopAsync();
        }
    }

    // ── harness ─────────────────────────────────────────────────

    private sealed class RelayPod
    {
        public required LeaderElector Elector { get; init; }
        public required TranslationRoomRedisSubscriberService Subscriber { get; init; }
        public required IConnectionMultiplexer Connection { get; init; }
        public required Mock<IClientProxy> Proxy { get; init; }
        public bool Crashed { get; set; }

        public int Sends => Proxy.Invocations.Count(i => i.Method.Name == nameof(IClientProxy.SendCoreAsync));

        public async Task StopAsync()
        {
            await Subscriber.StopAsync(CancellationToken.None);
            await Elector.StopAsync(CancellationToken.None);
        }

        public async Task CrashAsync()
        {
            // Closing the connection mid-lease: no renewals, no release, no further deliveries.
            Crashed = true;
            await Connection.CloseAsync(allowCommandsToComplete: false);
        }
    }

    private async Task<RelayPod> StartRelayPodAsync(LeaderElectionOptions options)
    {
        var connection = await ConnectAsync();
        var eligibility = new PubSubSubscriptionEligibility(
            connection,
            [RealtimeRelay.Key(nameof(TranslationRoomRedisSubscriberService), RealtimeConstants.RedisChannels.TranslationRoomCommands)]);
        var elector = new LeaderElector(
            new DistributedLockProvider(new RedisLeaseStore(connection), TimeProvider.System),
            options,
            NullLogger<LeaderElector>.Instance,
            eligibility,
            connection);

        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        // Since WT-699 (TC1806) RoomEnded also reaches the room's lobby group. That copy goes to a
        // separate proxy so the counts below still measure relay broadcasts, one per message.
        var lobbyProxy = new Mock<IClientProxy>();
        lobbyProxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        clients.Setup(c => c.Group(It.Is<string>(g => g.EndsWith(":lobby")))).Returns(lobbyProxy.Object);
        var hub = new Mock<IHubContext<TranslationRoomHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        var subscriber = new TranslationRoomRedisSubscriberService(
            connection,
            hub.Object,
            NullLogger<TranslationRoomRedisSubscriberService>.Instance,
            new PubSubLeadership(elector, eligibility));

        await subscriber.StartAsync(CancellationToken.None);
        await elector.StartAsync(CancellationToken.None);
        return new RelayPod { Elector = elector, Subscriber = subscriber, Connection = connection, Proxy = proxy };
    }

    private static Task PublishRoomEndedAsync(IConnectionMultiplexer publisher)
        => publisher.GetSubscriber().PublishAsync(
            RedisChannel.Literal(RealtimeConstants.RedisChannels.TranslationRoomCommands),
            JsonSerializer.Serialize(new { Command = "RoomEnded", RoomId = Guid.NewGuid().ToString() }));

    private async Task<IConnectionMultiplexer> ConnectAsync()
    {
        var connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        _connections.Add(connection);
        return connection;
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? within = null)
    {
        using var timeout = new CancellationTokenSource(within ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }
}
