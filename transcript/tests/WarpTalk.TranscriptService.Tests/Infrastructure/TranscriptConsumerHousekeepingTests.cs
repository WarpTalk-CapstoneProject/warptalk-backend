using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using Testcontainers.Redis;
using WarpTalk.TranscriptService.Infrastructure.Redis;
using WarpTalk.TranscriptService.Tests.Helpers;
using static WarpTalk.TranscriptService.Infrastructure.Redis.TranscriptConsumerPollingPolicy;

namespace WarpTalk.TranscriptService.Tests.Infrastructure;

/// <summary>
/// The consumer name carries a per-process Guid and nothing ever removed one, so prod's
/// <c>transcript-persistence</c> group held 95 consumers on stt/translate/tts:results and 22 on
/// transcript:clean (1 Oct) — one per pod ever started. Same leak backend#487 fixed in the
/// gateway, with one difference that decides the design: these entries are transcript lines, so
/// a dead consumer is removed only once its pending list is empty, and emptying it is the
/// existing XAUTOCLAIM-and-persist recovery, never an XACK.
/// </summary>
public sealed class TranscriptConsumerHousekeepingTests
{
    private const string Group = "transcript-persistence";
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    [Fact]
    public void SelectDeadConsumers_TakesOnlyIdleForeignConsumersWithNothingPending()
    {
        var self = "transcript-pod-a-self";
        var consumers = new[]
        {
            new ConsumerState("dead", 0, (long)Hour.TotalMilliseconds + 1),
            new ConsumerState("exactly-an-hour", 0, (long)Hour.TotalMilliseconds),
            new ConsumerState("holder", 3, (long)Hour.TotalMilliseconds * 24),
            new ConsumerState("live", 0, 250),
            new ConsumerState(self, 0, (long)Hour.TotalMilliseconds * 24),
            new ConsumerState("", 0, (long)Hour.TotalMilliseconds * 24),
        };

        var selected = SelectDeadConsumers(consumers, self, Hour);

        Assert.Equal(new[] { "dead", "exactly-an-hour" }, selected);
    }

    [Fact]
    public void DeadConsumerIdle_IsFarAboveAnythingALiveConsumerProduces()
    {
        // The poll loop reads every stream each pass and backs off at most 5 s after an error.
        Assert.True(DeadConsumerIdle >= TimeSpan.FromHours(1));
        Assert.True(ConsumerHousekeepingInterval >= PendingRecoveryInterval);
    }

    [Fact]
    public async Task Housekeeping_RunsOncePerInterval_AndNeverThrowsIntoThePollLoop()
    {
        var database = Substitute.For<IDatabase>();
        database.StreamConsumerInfoAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisServerException("NOGROUP No such key"));
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        var service = new TranscriptRedisConsumerService(
            redis,
            NullLogger<TranscriptRedisConsumerService>.Instance,
            new ServiceCollection().BuildServiceProvider());
        var housekeep = typeof(TranscriptRedisConsumerService).GetMethod(
            "HousekeepConsumersAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        await (Task)housekeep.Invoke(service, [database, "stt:results"])!;
        await (Task)housekeep.Invoke(service, [database, "stt:results"])!;
        await (Task)housekeep.Invoke(service, [database, "tts:results"])!;

        // Once per stream per interval — the second stt:results pass is throttled.
        await database.Received(1).StreamConsumerInfoAsync("stt:results", Group, Arg.Any<CommandFlags>());
        await database.Received(1).StreamConsumerInfoAsync("tts:results", Group, Arg.Any<CommandFlags>());
        await database.DidNotReceiveWithAnyArgs().StreamDeleteConsumerAsync(default, default, default, default);
    }
}

/// <summary>The same pass against a real Redis — XINFO CONSUMERS idle semantics, XAUTOCLAIM, DELCONSUMER.</summary>
public sealed class TranscriptConsumerHousekeepingIntegrationTests : IAsyncLifetime
{
    private const string Group = "transcript-persistence";

    private readonly RedisContainer _redis = new RedisBuilder().WithImage("redis:7-alpine").Build();
    private IConnectionMultiplexer? _connection;
    private bool _started;

    /// <summary>
    /// Against a real Redis: a dead consumer that finished its work is removed; one that died
    /// holding a transcript line is NOT, until the stale-pending claim moves that line onto a live
    /// consumer (which persists it); then it goes too. The live poller and the caller survive.
    ///
    /// "live" last read something as long ago as the dead ones did and has polled with empty
    /// XREADGROUPs since, exactly as the service does on a quiet stream. It surviving pins that
    /// XINFO CONSUMERS <c>idle</c> counts attempted reads (Redis 7.2+).
    /// </summary>
    [DockerFact]
    public async Task DeadConsumersAreRemoved_OnlyAfterTheirPendingLinesAreClaimed()
    {
        _connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var db = _connection.GetDatabase();
        const string stream = "stt:results";
        const string self = "transcript-self";

        await db.StreamCreateConsumerGroupAsync(stream, Group, "0-0", true);
        // One line for each of the four consumers below.
        for (var i = 0; i < 4; i++)
        {
            await db.StreamAddAsync(stream, [new NameValueEntry("text", $"line {i}")]);
        }

        var deadRead = await db.StreamReadGroupAsync(stream, Group, "dead", ">", count: 1);
        await db.StreamAcknowledgeAsync(stream, Group, deadRead[0].Id);
        var held = await db.StreamReadGroupAsync(stream, Group, "holder", ">", count: 1);
        var liveRead = await db.StreamReadGroupAsync(stream, Group, "live", ">", count: 1);
        await db.StreamAcknowledgeAsync(stream, Group, liveRead[0].Id);
        var selfRead = await db.StreamReadGroupAsync(stream, Group, self, ">", count: 1);
        await db.StreamAcknowledgeAsync(stream, Group, selfRead[0].Id);

        var until = DateTime.UtcNow.AddMilliseconds(900);
        while (DateTime.UtcNow < until)
        {
            Assert.Empty(await db.StreamReadGroupAsync(stream, Group, "live", ">", count: 1));
            await Task.Delay(50);
        }

        var minIdle = TimeSpan.FromMilliseconds(600);
        var logger = NullLogger.Instance;

        var firstPass = await TranscriptRedisConsumerService.RemoveDeadConsumersAsync(db, stream, self, minIdle, logger);
        Assert.Equal(new[] { "dead" }, firstPass);
        Assert.Equal(1, (await db.StreamPendingAsync(stream, Group)).PendingMessageCount);

        // What RecoverStaleMessagesAsync does: claim, then process and ACK. The line is moved,
        // not dropped.
        var claimed = await db.StreamAutoClaimAsync(stream, Group, self, 500, "0-0", 10);
        Assert.Equal(held[0].Id, Assert.Single(claimed.ClaimedEntries).Id);

        var secondPass = await TranscriptRedisConsumerService.RemoveDeadConsumersAsync(db, stream, self, minIdle, logger);
        Assert.Equal(new[] { "holder" }, secondPass);

        var remaining = (await db.StreamConsumerInfoAsync(stream, Group)).Select(c => c.Name.ToString()).Order().ToArray();
        Assert.Equal(new[] { "live", self }, remaining);
        var stillOwed = Assert.Single(await db.StreamPendingMessagesAsync(stream, Group, 10, self));
        Assert.Equal(held[0].Id, stillOwed.MessageId);
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _redis.StartAsync();
            _started = true;
        }
        catch
        {
            // Without Docker the DockerFact test is skipped before it runs.
        }
    }

    public async Task DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
        if (_started) await _redis.DisposeAsync();
    }
}
