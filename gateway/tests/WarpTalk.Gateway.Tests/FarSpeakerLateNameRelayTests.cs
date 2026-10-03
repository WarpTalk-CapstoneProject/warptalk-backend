using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Gateway.Services;
using WarpTalk.Shared;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// The LATE name of a bridge stand-in line: the line went out as "Google Meet participants"
/// because the Meet captions naming its speaker had not arrived yet, and stt_worker publishes the
/// name on <c>stt:far_speaker_late</c> about a second later. The Gateway relays it to the room as
/// TranscriptSegmentSpeakerNamed <c>{ segmentId, speakerName }</c> — under the same confidence
/// gate as the line itself (<see cref="AiResultConsumerService.TryResolveStandInSpeakerName"/>), so
/// a late entry can never show a name the line would have hidden.
/// </summary>
public sealed class FarSpeakerLateNameRelayTests
{
    private const string RoomId = "8f14e45f-ce0a-4a4f-9f5b-2c3d4e5f6a7b";
    private const string Stream = "stt:far_speaker_late";
    private static readonly Guid SegmentId = Guid.Parse("3b8c1a52-6d0e-4f7a-9a51-0c2d6b7e8f90");
    private static readonly string StandIn = ExternalBridgeConstants.ParticipantUserId.ToString();

    private static StreamEntry Entry(params (string Name, string Value)[] fields) =>
        new("1-0", fields.Select(f => new NameValueEntry(f.Name, f.Value)).ToArray());

    /// <summary>The wire contract's entry, as stt_worker publishes it.</summary>
    private static (string Name, string Value)[] LateFields(string name = "Alice Nguyen", string confidence = "0.82") =>
    [
        ("type", "far_speaker_late"),
        ("meeting_id", RoomId),
        ("segment_id", SegmentId.ToString()),
        ("far_speaker_name", name),
        ("far_speaker_source", "meet_caption"),
        ("far_speaker_confidence", confidence),
        ("t_ms", "1790000000000"),
    ];

    // ── The contract ─────────────────────────────────────────

    [Fact]
    public void TheStreamName_IsTheContract()
    {
        // Fixed with warptalk-ai (producer) and TranscriptService (the saved row): a rename on one
        // side silently stops late names reaching the other.
        Assert.Equal("stt:far_speaker_late", FarSpeakerNames.LateNameStream);
        Assert.Contains(FarSpeakerNames.LateNameStream, AiResultConsumerService.ConsumedStreams);
    }

    [Fact]
    public void ThePayload_SerializesAsSegmentIdAndSpeakerName()
    {
        // SignalR's JSON protocol serialises with the web defaults (camelCase); the web client reads
        // exactly these two keys.
        var json = JsonSerializer.Serialize(
            new TranscriptSegmentSpeakerNamedDto(SegmentId, "Alice Nguyen"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal($"{{\"segmentId\":\"{SegmentId}\",\"speakerName\":\"Alice Nguyen\"}}", json);
    }

    // ── The confidence gate ──────────────────────────────────

    [Theory]
    [InlineData("0.6", "Alice Nguyen")]
    [InlineData("0.82", "Alice Nguyen")]
    [InlineData("1", "Alice Nguyen")]
    [InlineData("0.59", null)]
    [InlineData("", null)]
    [InlineData("not-a-number", null)]
    [InlineData("1.5", null)]
    public void LateName_IsShownOnlyAtOrAboveTheThreshold(string confidence, string? expected)
    {
        var named = AiResultConsumerService.TryReadLateSpeakerName(
            Entry(LateFields(confidence: confidence)), FarSpeakerNames.DefaultMinConfidence);

        Assert.Equal(expected, named?.SpeakerName);
    }

    [Fact]
    public void LateName_FollowsAStricterConfiguredThreshold()
    {
        // stt_worker sends at or above its own 0.6; a deployment configured stricter must still hide
        // what its lines would hide.
        Assert.Null(AiResultConsumerService.TryReadLateSpeakerName(Entry(LateFields(confidence: "0.7")), 0.8));
        Assert.NotNull(AiResultConsumerService.TryReadLateSpeakerName(Entry(LateFields(confidence: "0.85")), 0.8));
    }

    [Fact]
    public void LateName_IsTrimmedAndTruncatedToTheColumn()
    {
        Assert.Equal("Alice Nguyen",
            AiResultConsumerService.TryReadLateSpeakerName(Entry(LateFields(name: "  Alice Nguyen ")), 0.6)?.SpeakerName);
        Assert.Equal(FarSpeakerNames.MaxLength,
            AiResultConsumerService.TryReadLateSpeakerName(Entry(LateFields(name: new string('a', 150))), 0.6)?.SpeakerName.Length);
    }

    [Theory]
    [InlineData("segment_id", "")]
    [InlineData("segment_id", "not-a-guid")]
    [InlineData("segment_id", "00000000-0000-0000-0000-000000000000")]
    [InlineData("far_speaker_name", "")]
    [InlineData("far_speaker_name", "   ")]
    public void AnEntryWithNoSegmentOrNoName_IsNotShown(string field, string value)
    {
        var fields = LateFields().Select(f => f.Name == field ? (f.Name, value) : f).ToArray();

        Assert.Null(AiResultConsumerService.TryReadLateSpeakerName(Entry(fields), 0.6));
    }

    [Fact]
    public void ASpeakerIdOtherThanTheStandIn_IsRefused()
    {
        // Not in the contract, but a producer that adds it must not be able to rename a real
        // participant's line from a Meet caption.
        var person = LateFields().Append(("speaker_id", Guid.NewGuid().ToString())).ToArray();
        var standIn = LateFields().Append(("speaker_id", StandIn)).ToArray();

        Assert.Null(AiResultConsumerService.TryReadLateSpeakerName(Entry(person), 0.6));
        Assert.NotNull(AiResultConsumerService.TryReadLateSpeakerName(Entry(standIn), 0.6));
    }

    // ── The broadcast ────────────────────────────────────────

    [Fact]
    public async Task Broadcast_GoesToTheRoom_AsTranscriptSegmentSpeakerNamed()
    {
        await using var harness = new Harness(config: null, LateFields());

        var (group, method, payload) = await harness.FirstSendAsync();

        Assert.Equal($"translationRoom:{RoomId}", group);
        Assert.Equal("TranscriptSegmentSpeakerNamed", method);
        var named = Assert.IsType<TranscriptSegmentSpeakerNamedDto>(payload);
        Assert.Equal(SegmentId, named.SegmentId);
        Assert.Equal("Alice Nguyen", named.SpeakerName);
        Assert.Equal("1-0", await harness.FirstAckAsync());
    }

    [Fact]
    public async Task UnderTheConfiguredThreshold_NothingIsBroadcast_ButTheEntryIsAcked()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [FarSpeakerNames.MinConfidenceConfigKey] = "0.9" })
            .Build();
        await using var harness = new Harness(config, LateFields(confidence: "0.82"));

        Assert.Equal("1-0", await harness.FirstAckAsync());
        Assert.False(harness.AnythingSent);
    }

    [Fact]
    public async Task AnEntryWithNoRoom_IsAcked_NotBroadcast()
    {
        await using var harness = new Harness(config: null, LateFields().Where(f => f.Name != "meeting_id").ToArray());

        Assert.Equal("1-0", await harness.FirstAckAsync());
        Assert.False(harness.AnythingSent);
    }

    /// <summary>
    /// The real AiResultConsumerService over a mocked Redis with one entry on the late-name stream
    /// and nothing anywhere else. Records every hub send and every XACK on that stream.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly AiResultConsumerService _service;
        private readonly Mock<IDatabase> _database = new();
        private readonly Mock<IHubClients> _clients = new();
        private readonly List<(string Group, string Method, object? Payload)> _sends = new();
        private readonly TaskCompletionSource<string> _acked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly StreamEntry _entry;
        private int _reads;
        private bool _started;

        public Harness(IConfiguration? config, (string Name, string Value)[] fields)
        {
            _entry = Entry(fields);
            var redis = new Mock<IConnectionMultiplexer>();
            redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_database.Object);

            _database
                .Setup(d => d.StreamCreateConsumerGroupAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(),
                    It.IsAny<bool>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(true);
            _database
                .Setup(d => d.StreamReadGroupAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(),
                    It.IsAny<int?>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, RedisValue _, RedisValue __, RedisValue? ___, int? ____, CommandFlags _____) =>
                    Task.FromResult(NextBatch(key.ToString())));
            _database
                .Setup(d => d.StreamReadGroupAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(),
                    It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, RedisValue _, RedisValue __, RedisValue? ___, int? ____, bool _____, CommandFlags ______) =>
                    Task.FromResult(NextBatch(key.ToString())));
            _database
                .Setup(d => d.StreamReadGroupAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
                    It.IsAny<RedisValue?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                    It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, RedisValue _, RedisValue __, RedisValue? ___, int? ____,
                          bool _____, TimeSpan? ______, CommandFlags _______) =>
                    Task.FromResult(NextBatch(key.ToString())));
            _database
                .Setup(d => d.StreamAcknowledgeAsync(Stream, It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Callback<RedisKey, RedisValue, RedisValue, CommandFlags>((_, _, id, _) => _acked.TrySetResult(id.ToString()))
                .ReturnsAsync(1);

            _clients.Setup(c => c.Group(It.IsAny<string>())).Returns((string group) =>
            {
                var proxy = new Mock<IClientProxy>();
                proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                    .Callback<string, object?[], CancellationToken>((method, args, _) =>
                    {
                        lock (_sends) _sends.Add((group, method, args.Length > 0 ? args[0] : null));
                    })
                    .Returns(Task.CompletedTask);
                return proxy.Object;
            });
            var hubContext = new Mock<IHubContext<TranslationRoomHub>>();
            hubContext.Setup(c => c.Clients).Returns(_clients.Object);

            _service = new AiResultConsumerService(
                new RedisStreamService(redis.Object, NullLogger<RedisStreamService>.Instance, new ConfigurationBuilder().Build()),
                new ActiveTranslationRoomRegistry(),
                hubContext.Object,
                // Never reached: this relay looks up no room or workspace policy.
                null!,
                null!,
                NullLogger<AiResultConsumerService>.Instance,
                config);
        }

        public bool AnythingSent
        {
            get { lock (_sends) return _sends.Count > 0; }
        }

        private StreamEntry[] NextBatch(string streamKey)
        {
            if (streamKey != Stream || Interlocked.Increment(ref _reads) > 1)
                return Array.Empty<StreamEntry>();
            return new[] { _entry };
        }

        private async Task StartAsync()
        {
            if (_started) return;
            _started = true;
            await _service.StartAsync(CancellationToken.None);
        }

        public async Task<string> FirstAckAsync()
        {
            await StartAsync();
            var completed = await Task.WhenAny(_acked.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            if (completed != _acked.Task)
                throw new TimeoutException("The late-name entry was never acknowledged.");
            return await _acked.Task;
        }

        public async Task<(string Group, string Method, object? Payload)> FirstSendAsync()
        {
            await StartAsync();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (_sends)
                {
                    if (_sends.Count > 0) return _sends[0];
                }
                await Task.Delay(10);
            }

            throw new TimeoutException("Nothing was broadcast.");
        }

        public async ValueTask DisposeAsync()
        {
            if (_started) await _service.StopAsync(CancellationToken.None);
        }
    }
}
