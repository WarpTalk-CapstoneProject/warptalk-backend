using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Gateway.Presence;
using WarpTalk.Gateway.Services;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// Live Meet speaker names from the bridge capturer's desktop into
/// <c>meeting:{room}:far_speaker_hints</c>, the stream warptalk-ai's CaptionHintTracker reads.
/// </summary>
public sealed class FarSpeakerHintIngestTests
{
    private const long ServerNow = 1_759_300_000_000;
    private static readonly Guid RoomId = Guid.Parse("8f14e45f-ce0a-4a4f-9f5b-2c3d4e5f6a7b");

    private sealed class ManualTime(long nowMs) : TimeProvider
    {
        public long NowMs = nowMs;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(NowMs);
    }

    private static FarSpeakerHintDto Hint(string name, long start, long end, string? confidence = null) =>
        new(name, start, end, confidence);

    // ── Validation ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("  Lan   Pham ", "Lan Pham")]
    [InlineData("Nguyễn Văn A", "Nguyễn Văn A")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("Lan\u0000Pham", null)]
    [InlineData("Lan\nPham", null)]
    [InlineData("Lan\u001bPham", null)]
    public void NormalizeName_TrimsCollapsesAndRejectsControlChars(string raw, string? expected)
    {
        Assert.Equal(expected, FarSpeakerHintIngest.NormalizeName(raw));
    }

    [Fact]
    public void NormalizeName_EnforcesTheLengthBound()
    {
        Assert.NotNull(FarSpeakerHintIngest.NormalizeName(new string('a', 80)));
        Assert.Null(FarSpeakerHintIngest.NormalizeName(new string('a', 81)));
        Assert.Null(FarSpeakerHintIngest.NormalizeName(null));
    }

    [Fact]
    public void Expand_DropsHintsOlderThan30Seconds_AndFromTheFuture_AndInverted()
    {
        var hw = new Dictionary<string, long>();
        var entries = FarSpeakerHintIngest.Expand(
            [
                Hint("Old", ServerNow - 40_000, ServerNow - 30_001, "batch"),
                Hint("Future", ServerNow + 5_000, ServerNow + 6_000),
                Hint("Inverted", ServerNow - 1_000, ServerNow - 2_000),
                Hint("Zero", 0, ServerNow),
                Hint("Recent", ServerNow - 29_000, ServerNow - 28_000, "batch"),
            ],
            clientNowMs: ServerNow,
            serverNowMs: ServerNow,
            hw);

        Assert.All(entries, e => Assert.Equal("Recent", e.Name));
        Assert.NotEmpty(entries);
    }

    [Fact]
    public void Expand_KeepsOnlyTheNewest20HintsOfACall()
    {
        var hints = Enumerable.Range(0, 25)
            .Select(i => Hint($"P{i}", ServerNow - 25_000 + i * 1_000, ServerNow - 25_000 + i * 1_000 + 100))
            .ToArray();

        var entries = FarSpeakerHintIngest.Expand(hints, ServerNow, ServerNow, new Dictionary<string, long>());

        var names = entries.Select(e => e.Name).Distinct().ToList();
        Assert.Equal(20, names.Count);
        Assert.DoesNotContain("P0", names);
        Assert.DoesNotContain("P4", names);
        Assert.Contains("P24", names);
    }

    // ── Clock alignment ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Expand_MovesClientTimestampsOntoTheServerClock()
    {
        // The desktop's clock runs 7 s behind the server.
        const long clientNow = ServerNow - 7_000;
        var entries = FarSpeakerHintIngest.Expand(
            [Hint("Lan", clientNow - 2_000, clientNow - 1_000)],
            clientNow,
            ServerNow,
            new Dictionary<string, long>());

        Assert.Equal(
            new[] { ServerNow - 2_000, ServerNow - 1_500, ServerNow - 1_000 },
            entries.Select(e => e.TMs).ToArray());
    }

    [Fact]
    public void Expand_ClockAhead_IsNotMistakenForAFutureHint()
    {
        // The desktop's clock runs an hour ahead; its "now" is still the server's now.
        const long clientNow = ServerNow + 3_600_000;
        var entries = FarSpeakerHintIngest.Expand(
            [Hint("Lan", clientNow - 600, clientNow - 100)],
            clientNow,
            ServerNow,
            new Dictionary<string, long>());

        Assert.Single(entries);
        Assert.Equal(ServerNow - 500, entries[0].TMs);
    }

    // ── Expansion ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Expand_ALongBlockCastsProportionallyMoreVotes()
    {
        var entries = FarSpeakerHintIngest.Expand(
            [Hint("Long", ServerNow - 5_000, ServerNow - 3_000), Hint("Short", ServerNow - 2_600, ServerNow - 2_400)],
            ServerNow,
            ServerNow,
            new Dictionary<string, long>());

        Assert.Equal(5, entries.Count(e => e.Name == "Long"));   // -5000 .. -3000 every 500
        Assert.Equal(1, entries.Count(e => e.Name == "Short"));  // straddles -2500
        Assert.Equal(entries.OrderBy(e => e.TMs).ToList(), entries);
    }

    [Fact]
    public void Expand_ASpanWithNoGridPoint_GetsOneEntryAtItsMiddle()
    {
        var start = ServerNow - 1_400; // grid points at -1500 and -1000 both fall outside
        var entries = FarSpeakerHintIngest.Expand(
            [Hint("Quick", start, start + 200)],
            ServerNow,
            ServerNow,
            new Dictionary<string, long>());

        var only = Assert.Single(entries);
        Assert.Equal(start + 100, only.TMs);
    }

    [Fact]
    public void Expand_CapsOneHintAt20Entries_KeepingTheLatest()
    {
        var entries = FarSpeakerHintIngest.Expand(
            [Hint("Monologue", ServerNow - 29_000, ServerNow)],
            ServerNow,
            ServerNow,
            new Dictionary<string, long>());

        Assert.Equal(FarSpeakerHintIngest.MaxEntriesPerHint, entries.Count);
        Assert.Equal(ServerNow, entries[^1].TMs);
        Assert.Equal(ServerNow - 19 * 500, entries[0].TMs);
    }

    [Fact]
    public void Expand_ARereportedGrowingBlock_IsWrittenOnlyOnce()
    {
        var hw = new Dictionary<string, long>();
        var first = FarSpeakerHintIngest.Expand([Hint("Lan", ServerNow - 3_000, ServerNow - 2_000)], ServerNow, ServerNow, hw);
        var again = FarSpeakerHintIngest.Expand([Hint("Lan", ServerNow - 3_000, ServerNow - 2_000)], ServerNow, ServerNow, hw);
        var grown = FarSpeakerHintIngest.Expand([Hint("lan", ServerNow - 3_000, ServerNow - 1_000)], ServerNow, ServerNow, hw);

        Assert.Equal(3, first.Count);
        Assert.Empty(again);
        Assert.Equal(new[] { ServerNow - 1_500, ServerNow - 1_000 }, grown.Select(e => e.TMs).ToArray());
    }

    [Fact]
    public void GridPoints_AreAlignedToAbsoluteTime()
    {
        Assert.Equal(new long[] { 1_000, 1_500, 2_000 }, FarSpeakerHintIngest.GridPoints(901, 2_100));
        Assert.Empty(FarSpeakerHintIngest.GridPoints(1_001, 1_499));
        Assert.Empty(FarSpeakerHintIngest.GridPoints(2_000, 1_000));
    }

    // ── Ingest: authorization, rate limit, Redis write ────────────────────────────────────

    private static (FarSpeakerHintIngest Sut, Mock<IDatabase> Db, ManualTime Time) CreateIngest()
    {
        var db = new Mock<IDatabase>();
        db.Setup(d => d.StreamAddAsync(
                It.IsAny<RedisKey>(), It.IsAny<NameValueEntry[]>(), It.IsAny<RedisValue?>(),
                It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<StreamTrimMode>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisValue)"1-0");
        db.Setup(d => d.KeyExpireAsync(It.IsAny<RedisKey>(), It.IsAny<TimeSpan?>(), It.IsAny<ExpireWhen>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(db.Object);
        var time = new ManualTime(ServerNow);
        return (new FarSpeakerHintIngest(redis.Object, NullLogger<FarSpeakerHintIngest>.Instance, time), db, time);
    }

    private static Func<CancellationToken, Task<bool>> Allow(bool allowed) => _ => Task.FromResult(allowed);

    [Fact]
    public async Task Ingest_WritesTheAiContract_WithApproximateMaxLenAndTtl()
    {
        var (sut, db, _) = CreateIngest();

        var (outcome, written) = await sut.IngestAsync(
            RoomId, "capturer", [Hint("Lan Pham", ServerNow - 1_000, ServerNow - 500)], ServerNow, Allow(true));

        Assert.Equal(FarSpeakerHintOutcome.Accepted, outcome);
        Assert.Equal(2, written);
        var key = $"meeting:{RoomId}:far_speaker_hints";
        db.Verify(d => d.StreamAddAsync(
                key,
                It.Is<NameValueEntry[]>(f =>
                    f.Length == 3
                    && f[0].Name == "name" && f[0].Value == "Lan Pham"
                    && f[1].Name == "t_ms" && f[1].Value == (ServerNow - 1_000).ToString()
                    && f[2].Name == "source" && f[2].Value == "meet_caption"),
                null, (long?)FarSpeakerHintIngest.StreamMaxLength, true, It.IsAny<long?>(), It.IsAny<StreamTrimMode>(), CommandFlags.None),
            Times.Once);
        db.Verify(d => d.KeyExpireAsync(key, FarSpeakerHintIngest.StreamTtl, ExpireWhen.Always, CommandFlags.None), Times.Once);
    }

    [Fact]
    public async Task Ingest_Refused_WritesNothing_AndCachesTheAnswerBriefly()
    {
        var (sut, db, time) = CreateIngest();
        var asked = 0;
        Task<bool> Deny(CancellationToken _) { asked++; return Task.FromResult(false); }

        var first = await sut.IngestAsync(RoomId, "host", [Hint("Lan", ServerNow - 1_000, ServerNow)], ServerNow, Deny);
        var second = await sut.IngestAsync(RoomId, "host", [Hint("Lan", ServerNow - 1_000, ServerNow)], ServerNow, Deny);
        time.NowMs += 16_000;
        await sut.IngestAsync(RoomId, "host", [], time.NowMs, Deny);

        Assert.Equal(FarSpeakerHintOutcome.Refused, first.Outcome);
        Assert.Equal(FarSpeakerHintOutcome.Refused, second.Outcome);
        Assert.Equal(2, asked);
        db.Verify(d => d.StreamAddAsync(
                It.IsAny<RedisKey>(), It.IsAny<NameValueEntry[]>(), It.IsAny<RedisValue?>(),
                It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<StreamTrimMode>(), It.IsAny<CommandFlags>()),
            Times.Never);
    }

    [Fact]
    public async Task Ingest_AFailingAuthorizationCheck_Refuses()
    {
        var (sut, _, _) = CreateIngest();
        var (outcome, _) = await sut.IngestAsync(
            RoomId, "capturer", [], ServerNow, _ => throw new InvalidOperationException("grpc down"));
        Assert.Equal(FarSpeakerHintOutcome.Refused, outcome);
    }

    [Fact]
    public async Task Ingest_DropsCallsOverTenPerSecondPerRoom()
    {
        var (sut, _, time) = CreateIngest();
        var outcomes = new List<FarSpeakerHintOutcome>();
        for (var i = 0; i < 12; i++)
        {
            outcomes.Add((await sut.IngestAsync(RoomId, "capturer", [], ServerNow, Allow(true))).Outcome);
        }
        var otherRoom = await sut.IngestAsync(Guid.NewGuid(), "capturer", [], ServerNow, Allow(true));
        time.NowMs += 1_000;
        var nextSecond = await sut.IngestAsync(RoomId, "capturer", [], time.NowMs, Allow(true));

        Assert.Equal(10, outcomes.Count(o => o == FarSpeakerHintOutcome.Accepted));
        Assert.Equal(2, outcomes.Count(o => o == FarSpeakerHintOutcome.RateLimited));
        Assert.Equal(FarSpeakerHintOutcome.Accepted, otherRoom.Outcome);
        Assert.Equal(FarSpeakerHintOutcome.Accepted, nextSecond.Outcome);
    }

    [Fact]
    public async Task Ingest_ARedisFailure_DoesNotFailTheCall()
    {
        var (sut, db, _) = CreateIngest();
        db.Setup(d => d.StreamAddAsync(
                It.IsAny<RedisKey>(), It.IsAny<NameValueEntry[]>(), It.IsAny<RedisValue?>(),
                It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<StreamTrimMode>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"));

        var (outcome, written) = await sut.IngestAsync(
            RoomId, "capturer", [Hint("Lan", ServerNow - 1_000, ServerNow)], ServerNow, Allow(true));

        Assert.Equal(FarSpeakerHintOutcome.Accepted, outcome);
        Assert.Equal(0, written);
    }

    // ── Who may report: the real predicate the hub's gate uses ────────────────────────────

    private static GetTranslationRoomResponse Room(string type, string hostId, string capturerId = "", string status = "ACTIVE") =>
        new() { Id = RoomId.ToString(), HostId = hostId, BridgeCapturerUserId = capturerId, TranslationRoomType = type, Status = status };

    [Fact]
    public void Authorization_CapturerAllowed_HostNotCapturerRefused_NonBridgeRefused_EndedRefused()
    {
        var host = Guid.NewGuid().ToString();
        var capturer = Guid.NewGuid().ToString();
        var bridge = ExternalBridgeConstants.RoomType;

        Assert.True(RoomHostAuthority.IsExternalBridgeHost(Room(bridge, host, capturer), capturer));
        Assert.False(RoomHostAuthority.IsExternalBridgeHost(Room(bridge, host, capturer), host));
        Assert.False(RoomHostAuthority.IsExternalBridgeHost(Room("MEETING", host, capturer), capturer));
        Assert.False(RoomHostAuthority.IsExternalBridgeHost(Room(bridge, host, capturer, "ENDED"), capturer));
    }

    // ── The hub method ────────────────────────────────────────────────────────────────────

    private static (TranslationRoomHub Hub, Mock<IDatabase> Db) CreateHub(Mock<IRoomHostAuthority> authority, string userId)
    {
        var db = new Mock<IDatabase>();
        db.Setup(d => d.StreamAddAsync(
                It.IsAny<RedisKey>(), It.IsAny<NameValueEntry[]>(), It.IsAny<RedisValue?>(),
                It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<StreamTrimMode>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisValue)"1-0");
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(db.Object);
        var config = new ConfigurationBuilder().Build();

        var hub = new TranslationRoomHub(
            Mock.Of<IConnectionManager>(),
            Mock.Of<IPresenceNotifier>(),
            new RedisStreamService(redis.Object, NullLogger<RedisStreamService>.Instance, config),
            new ActiveTranslationRoomRegistry(),
            redis.Object,
            authority.Object,
            Mock.Of<IRoomLanguagePolicy>(),
            new FarSpeakerHintIngest(redis.Object, NullLogger<FarSpeakerHintIngest>.Instance),
            NullLogger<TranslationRoomHub>.Instance);

        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId)], "Test")));
        context.Setup(c => c.ConnectionId).Returns("conn-1");
        hub.Context = context.Object;
        return (hub, db);
    }

    [Fact]
    public async Task Hub_Capturer_WritesHints_AskingAboutItsOwnIdentity()
    {
        var authority = new Mock<IRoomHostAuthority>();
        authority.Setup(a => a.CanSetExternalMeetingLanguageAsync(RoomId, "capturer-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var (hub, db) = CreateHub(authority, "capturer-1");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var written = await hub.ReportFarSpeakerHints(RoomId, [Hint("Lan", now - 1_200, now - 200)], now);

        Assert.InRange(written, 2, 3);
        db.Verify(d => d.StreamAddAsync(
                (RedisKey)$"meeting:{RoomId}:far_speaker_hints", It.IsAny<NameValueEntry[]>(), It.IsAny<RedisValue?>(),
                It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<StreamTrimMode>(), It.IsAny<CommandFlags>()),
            Times.Exactly(written));
    }

    [Fact]
    public async Task Hub_NotTheCapturer_GetsAHubException_AndNothingIsWritten()
    {
        var authority = new Mock<IRoomHostAuthority>();
        authority.Setup(a => a.CanSetExternalMeetingLanguageAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var (hub, db) = CreateHub(authority, "host-not-capturer");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await Assert.ThrowsAsync<HubException>(() =>
            hub.ReportFarSpeakerHints(RoomId, [Hint("Lan", now - 1_000, now)], now));

        db.Verify(d => d.StreamAddAsync(
                It.IsAny<RedisKey>(), It.IsAny<NameValueEntry[]>(), It.IsAny<RedisValue?>(),
                It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<long?>(), It.IsAny<StreamTrimMode>(), It.IsAny<CommandFlags>()),
            Times.Never);
    }
}
