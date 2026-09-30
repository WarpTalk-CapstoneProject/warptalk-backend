using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Testcontainers.Redis;
using WarpTalk.Gateway.Services;
using WarpTalk.Gateway.Tests.Helpers;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// The WarpTalkAiPendingStuck housekeeping against a real Redis: XAUTOCLAIM + XACK, XINFO
/// CONSUMERS idle semantics and XGROUP DELCONSUMER. See StalePendingHousekeepingTests for the
/// incident.
/// </summary>
public sealed class StalePendingHousekeepingIntegrationTests : IAsyncLifetime
{
    private const string Group = "gateway-consumers";

    private readonly RedisContainer _redis = new RedisBuilder().WithImage("redis:7-alpine").Build();
    private IConnectionMultiplexer? _connection;
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
            // Without Docker every test that needs it is skipped by DockerFact before it runs.
        }
    }

    public async Task DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
        if (_started) await _redis.DisposeAsync();
    }

    /// <summary>
    /// One pass: the stale entry a dead consumer was holding is acknowledged, then that consumer
    /// and the other long-idle one are removed — while the live consumer and the caller survive.
    ///
    /// "live" last READ something as long ago as the dead ones did, and has polled with empty
    /// XREADGROUPs since, exactly as the gateway does on a quiet stream. It surviving is what pins
    /// that XINFO CONSUMERS <c>idle</c> counts attempted reads (Redis 7.2+; <c>inactive</c> would
    /// not), which is the assumption the one-hour threshold rests on.
    /// </summary>
    [DockerFact]
    public async Task Housekeeping_RetiresStalePending_AndRemovesOnlyDeadForeignConsumers()
    {
        _connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var db = _connection.GetDatabase();
        var streams = new RedisStreamService(_connection, NullLogger<RedisStreamService>.Instance, new ConfigurationBuilder().Build());
        const string stream = "ai_assistant:results";

        await streams.EnsureConsumerGroupAsync(stream, Group);
        for (var i = 0; i < 3; i++)
            await db.StreamAddAsync(stream, [new NameValueEntry("type", "summary"), new NameValueEntry("content", $"#{i}")]);

        // dead: read and acked, then its pod went away. holder: read and never acked (the bug).
        // live: read and acked, then kept polling.
        var deadRead = await db.StreamReadGroupAsync(stream, Group, "dead", ">", count: 1);
        await db.StreamAcknowledgeAsync(stream, Group, deadRead[0].Id);
        await db.StreamReadGroupAsync(stream, Group, "holder", ">", count: 1);
        var liveRead = await db.StreamReadGroupAsync(stream, Group, "live", ">", count: 1);
        await db.StreamAcknowledgeAsync(stream, Group, liveRead[0].Id);

        var until = DateTime.UtcNow.AddMilliseconds(900);
        while (DateTime.UtcNow < until)
        {
            Assert.Empty(await db.StreamReadGroupAsync(stream, Group, "live", ">", count: 1));
            await Task.Delay(50);
        }

        var retired = await streams.AcknowledgeStalePendingAsync(stream, Group, "self", TimeSpan.FromMilliseconds(500));
        Assert.Equal(1L, retired);
        Assert.Equal(0, (await db.StreamPendingAsync(stream, Group)).PendingMessageCount);

        var removed = await streams.DeleteDeadConsumersAsync(stream, Group, "self", TimeSpan.FromMilliseconds(600));
        Assert.Equal(new[] { "dead", "holder" }, removed.Order().ToArray());

        var remaining = (await db.StreamConsumerInfoAsync(stream, Group)).Select(c => c.Name.ToString()).Order().ToArray();
        Assert.Equal(new[] { "live", "self" }, remaining);

        // A second replica running the same pass right after finds nothing left to do.
        Assert.Equal(0L, await streams.AcknowledgeStalePendingAsync(stream, Group, "replica-b", TimeSpan.FromMilliseconds(500)));
    }
}
