using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Gateway.Services;
using ConsumerState = WarpTalk.Gateway.Services.RedisStreamService.StreamConsumerState;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// WarpTalkAiPendingStuck: on 30 Sep, ai_assistant:results / gateway-consumers held 62 entries
/// pending forever, spread over 12 consumers of pods that no longer existed, in a group of 97
/// consumers — one per gateway process ever started.
///
/// Two defects, both covered here. The consume loops skipped an entry with no meeting_id WITHOUT
/// acknowledging it, so it stayed pending on that consumer for good; and nothing ever reclaimed
/// entries left pending by a dead consumer, or removed the consumer itself.
/// </summary>
public sealed class StalePendingHousekeepingTests
{
    private const string Group = "gateway-consumers";
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    // ── Which consumers may be deleted ───────────────────────

    [Fact]
    public void SelectDeadConsumers_TakesOnlyIdleZeroPendingForeignConsumers()
    {
        var consumers = new[]
        {
            new ConsumerState("gateway-old-pod-1", PendingCount: 0, IdleMs: 3 * 3_600_000),
            new ConsumerState("gateway-old-pod-2", PendingCount: 0, IdleMs: 3_600_000),
            new ConsumerState("gateway-holds-work", PendingCount: 4, IdleMs: 5 * 3_600_000),
            new ConsumerState("gateway-other-replica", PendingCount: 0, IdleMs: 180),
            new ConsumerState("gateway-just-under", PendingCount: 0, IdleMs: 3_599_999),
        };

        var dead = RedisStreamService.SelectDeadConsumers(consumers, "gateway-self", Hour);

        Assert.Equal(new[] { "gateway-old-pod-1", "gateway-old-pod-2" }, dead);
    }

    [Fact]
    public void SelectDeadConsumers_NeverSelectsItself_EvenWhenIdleAndEmpty()
    {
        var consumers = new[] { new ConsumerState("gateway-self", PendingCount: 0, IdleMs: 10 * 3_600_000) };

        Assert.Empty(RedisStreamService.SelectDeadConsumers(consumers, "gateway-self", Hour));
    }

    /// <summary>
    /// A housekeeping pass covers the streams in <see cref="AiResultConsumerService.ConsumedStreams"/>,
    /// so a seventh consume loop that is not added there would quietly go back to accumulating
    /// stuck entries.
    /// </summary>
    [Fact]
    public void ConsumedStreams_MatchesEveryConsumeLoop()
    {
        var source = File.ReadAllText(FindSourceFile("gateway/src/WarpTalk.Gateway/Services/AiResultConsumerService.cs"));
        var loopStreams = Regex.Matches(source, @"var streamKey = ""([^""]+)"";")
            .Select(m => m.Groups[1].Value)
            .Order()
            .ToArray();

        Assert.NotEmpty(loopStreams);
        Assert.Equal(loopStreams, AiResultConsumerService.ConsumedStreams.Order().ToArray());
    }

    // ── Unroutable entries are acknowledged ──────────────────

    /// <summary>
    /// The production shape: a summary from ai_assistant_worker with only type, content and
    /// timestamp_ms. Every loop must XACK it rather than leave it pending on its consumer.
    /// </summary>
    [Theory]
    [InlineData("stt:results")]
    [InlineData("translate:results")]
    [InlineData("tts:results")]
    [InlineData("ai_assistant:results")]
    [InlineData("transcript:clean")]
    [InlineData("voice:clone:state")]
    public async Task EntryWithoutMeetingId_IsAcknowledged(string streamKey)
    {
        var entry = new StreamEntry(
            "1727654400000-0",
            [new("type", "summary"), new("content", "…"), new("timestamp_ms", "1727654400000")]);

        var acked = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var db = new Mock<IDatabase>();
        SetupReadGroupOnce(db, streamKey, entry);
        db.Setup(d => d.StreamAcknowledgeAsync(streamKey, Group, It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .Callback<RedisKey, RedisValue, RedisValue, CommandFlags>((_, _, id, _) => acked.TrySetResult(id))
            .ReturnsAsync(1);

        var service = BuildService(db);
        await service.StartAsync(CancellationToken.None);
        var completed = await Task.WhenAny(acked.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        await service.StopAsync(CancellationToken.None);

        Assert.Same(acked.Task, completed);
        Assert.Equal("1727654400000-0", (await acked.Task).ToString());
    }

    // ── Helpers ──────────────────────────────────────────────

    private static void SetupReadGroupOnce(Mock<IDatabase> db, string streamKey, StreamEntry entry)
    {
        // Hand the entry out once, across whichever key-based overload ConsumeAsync binds to.
        var delivered = 0;
        StreamEntry[] Next() => Interlocked.Exchange(ref delivered, 1) == 0 ? [entry] : [];
        db.Setup(d => d.StreamReadGroupAsync(
                streamKey, It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(),
                It.IsAny<int?>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(Next);
        db.Setup(d => d.StreamReadGroupAsync(
                streamKey, It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(),
                It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(Next);
        db.Setup(d => d.StreamReadGroupAsync(
                streamKey, It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(),
                It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(Next);
    }

    private static AiResultConsumerService BuildService(Mock<IDatabase> db)
    {
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(db.Object);

        var hubContext = new Mock<IHubContext<TranslationRoomHub>>();
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(Mock.Of<IClientProxy>());
        hubContext.Setup(c => c.Clients).Returns(clients.Object);

        return new AiResultConsumerService(
            new RedisStreamService(redis.Object, NullLogger<RedisStreamService>.Instance, new ConfigurationBuilder().Build()),
            new ActiveTranslationRoomRegistry(),
            hubContext.Object,
            // Never reached: an unroutable entry is acknowledged before any room policy lookup.
            null!,
            null!,
            Mock.Of<ILogger<AiResultConsumerService>>());
    }

    private static string FindSourceFile(string relativePath)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(candidate))
                    return candidate;
                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException($"Could not locate {relativePath}.");
    }
}
