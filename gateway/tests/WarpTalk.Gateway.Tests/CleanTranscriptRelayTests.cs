using System.Text.Json;
using Grpc.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Gateway.Services;
using WarpTalk.Shared.Protos;
using TranscriptSegmentDto = WarpTalk.Gateway.Hubs.TranscriptSegmentDto;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// WT-716. The clean transcript on the live caption lane: tier 1 (cleanText/cleanFlags on
/// TranscriptSegmentReceived) and tier 2 (TranscriptCleanSentenceReceived).
///
/// Clean is the web's DEFAULT view, so two things here are not cosmetic: absent clean_text must
/// stay null (not "filler only", which would hide every line from an older stt_worker), and the
/// profanity filter must cover the clean text exactly as it covers the raw text, or the filter is
/// effectively off for most readers.
/// </summary>
public sealed class CleanTranscriptRelayTests
{
    private const string RoomId = "8f14e45f-ce0a-4a4f-9f5b-2c3d4e5f6a7b";
    private const string WorkspaceId = "1b3c5d7e-9f01-4234-8567-89abcdef0123";

    private static StreamEntry Entry(params (string Name, string Value)[] fields) =>
        new("1-0", fields.Select(f => new NameValueEntry(f.Name, f.Value)).ToArray());

    // ── Pure readers ─────────────────────────────────────────

    [Fact]
    public void TryReadCleanText_KeepsAbsentAndEmptyApart()
    {
        Assert.Null(AiResultConsumerService.TryReadCleanText(Entry(("text", "uh hello"))));
        Assert.Equal("", AiResultConsumerService.TryReadCleanText(Entry(("text", "uh"), ("clean_text", ""))));
        Assert.Equal("hello", AiResultConsumerService.TryReadCleanText(Entry(("clean_text", "hello"))));
    }

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData(" fillers_removed ,stutter_removed,,fillers_removed", new[] { "fillers_removed", "stutter_removed" })]
    [InlineData("escalate,some_future_flag", new[] { "escalate", "some_future_flag" })]
    public void ReadFlags_TrimsAndDedupes_NeverNull(string? raw, string[] expected)
    {
        Assert.Equal(expected, AiResultConsumerService.ReadFlags(raw));
    }

    [Fact]
    public void TryReadCleanSentence_MapsEveryField()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var sentenceId = Guid.NewGuid();
        var speaker = Guid.NewGuid();

        var dto = AiResultConsumerService.TryReadCleanSentence(Entry(
            ("meeting_id", RoomId),
            ("sentence_id", sentenceId.ToString()),
            ("revision", "3"),
            ("speaker_id", speaker.ToString()),
            ("segment_ids", JsonSerializer.Serialize(new[] { a.ToString(), b.ToString() })),
            ("clean_text", "We ship on Monday."),
            ("language", "en"),
            ("flags", "self_repair,escalate"),
            ("source", "llm"),
            ("timestamp_ms", "1785575730000")));

        Assert.NotNull(dto);
        Assert.Equal(sentenceId, dto!.Id);
        Assert.Equal(speaker, dto.SpeakerId);
        Assert.Equal([a, b], dto.SegmentIds);
        Assert.Equal("We ship on Monday.", dto.CleanText);
        Assert.Equal("en", dto.Language);
        Assert.Equal(["self_repair", "escalate"], dto.Flags);
        Assert.Equal("llm", dto.Source);
        Assert.Equal(3, dto.Revision);
    }

    [Theory]
    [InlineData("sentence_id")]
    [InlineData("revision")]
    [InlineData("segment_ids")]
    [InlineData("clean_text")]
    public void TryReadCleanSentence_IsNullWithoutARequiredField(string missing)
    {
        var fields = new List<(string, string)>
        {
            ("meeting_id", RoomId),
            ("sentence_id", Guid.NewGuid().ToString()),
            ("revision", "1"),
            ("segment_ids", JsonSerializer.Serialize(new[] { Guid.NewGuid().ToString() })),
            ("clean_text", "x"),
        }.Where(f => f.Item1 != missing).ToArray();

        Assert.Null(AiResultConsumerService.TryReadCleanSentence(Entry(fields)));
    }

    [Fact]
    public void TryReadCleanSentence_IsNullForASegmentListWithABadId()
    {
        Assert.Null(AiResultConsumerService.TryReadCleanSentence(Entry(
            ("sentence_id", Guid.NewGuid().ToString()),
            ("revision", "1"),
            ("segment_ids", $"[\"{Guid.NewGuid()}\",\"nope\"]"),
            ("clean_text", "x"))));
    }

    // ── The real loops over a mocked Redis ───────────────────

    [Fact]
    public async Task TheSegmentPayload_CarriesCleanTextAndFlags()
    {
        await using var harness = new Harness(profanityFilter: false, sttFields:
        [
            ("text", "uh so so we ship"),
            ("clean_text", "so we ship"),
            ("clean_flags", "fillers_removed,stutter_removed"),
        ]);

        var segment = await harness.FirstPayloadAsync<TranscriptSegmentDto>("TranscriptSegmentReceived");

        Assert.Equal("uh so so we ship", segment.OriginalText);
        Assert.Equal("so we ship", segment.CleanText);
        Assert.Equal(["fillers_removed", "stutter_removed"], segment.CleanFlags);
    }

    [Fact]
    public async Task TheSegmentPayload_SendsNullCleanText_AndAnEmptyFlagList_ForAnOlderProducer()
    {
        await using var harness = new Harness(profanityFilter: false, sttFields: [("text", "hello")]);

        var segment = await harness.FirstPayloadAsync<TranscriptSegmentDto>("TranscriptSegmentReceived");

        Assert.Null(segment.CleanText);
        Assert.NotNull(segment.CleanFlags);
        Assert.Empty(segment.CleanFlags!);
    }

    [Fact]
    public async Task TheProfanityFilter_MasksTheCleanTextToo()
    {
        await using var harness = new Harness(profanityFilter: true, sttFields:
        [
            ("text", "uh this is shit"),
            ("clean_text", "this is shit"),
        ]);

        var segment = await harness.FirstPayloadAsync<TranscriptSegmentDto>("TranscriptSegmentReceived");

        Assert.Equal("uh this is ***", segment.OriginalText);
        Assert.Equal("this is ***", segment.CleanText);
    }

    [Fact]
    public async Task ACleanSentence_IsRelayedToTheRoom_Masked()
    {
        await using var harness = new Harness(profanityFilter: true, sttFields: [("text", "x")]);

        var sentence = await harness.FirstPayloadAsync<TranscriptCleanSentenceDto>("TranscriptCleanSentenceReceived");

        Assert.Equal("That was a damn good call.".Replace("damn", "***"), sentence.CleanText);
        Assert.Equal(2, sentence.Revision);
        harness.Clients.Verify(c => c.Group($"translationRoom:{RoomId}"), Times.AtLeastOnce);
    }

    /// <summary>
    /// The real AiResultConsumerService over a mocked Redis: one stt:results entry and one
    /// transcript:clean entry, each delivered once. Same shape as TranscriptPauseGateTests' harness.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly AiResultConsumerService _service;
        private readonly Mock<IDatabase> _database = new();
        private readonly Mock<IClientProxy> _proxy = new();
        private readonly Dictionary<string, int> _reads = new();
        private readonly (string Name, string Value)[] _sttFields;

        public Mock<IHubClients> Clients { get; } = new();

        public Harness(bool profanityFilter, (string Name, string Value)[] sttFields)
        {
            _sttFields = sttFields;

            var redis = new Mock<IConnectionMultiplexer>();
            redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_database.Object);

            _database
                .Setup(d => d.StreamCreateConsumerGroupAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(),
                    It.IsAny<bool>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(true);

            // The eight-parameter overload — the one RedisStreamService.ConsumeAsync binds to. See
            // TranscriptPauseGateTests for why a setup on a shorter overload silently matches nothing.
            _database
                .Setup(d => d.StreamReadGroupAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
                    It.IsAny<RedisValue?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                    It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, RedisValue _, RedisValue __, RedisValue? ___, int? ____,
                          bool _____, TimeSpan? ______, CommandFlags _______) =>
                    Task.FromResult(NextBatch(key.ToString())));

            Clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_proxy.Object);
            var hubContext = new Mock<IHubContext<TranslationRoomHub>>();
            hubContext.Setup(c => c.Clients).Returns(Clients.Object);

            _service = new AiResultConsumerService(
                new RedisStreamService(redis.Object, NullLogger<RedisStreamService>.Instance, new ConfigurationBuilder().Build()),
                new ActiveTranslationRoomRegistry(),
                hubContext.Object,
                new FakeWorkspaceClient(profanityFilter),
                new FakeRoomClient(),
                NullLogger<AiResultConsumerService>.Instance);
        }

        private StreamEntry[] NextBatch(string streamKey)
        {
            lock (_reads)
            {
                _reads.TryGetValue(streamKey, out var seen);
                _reads[streamKey] = seen + 1;
                if (seen > 0)
                    return Array.Empty<StreamEntry>();
            }

            return streamKey switch
            {
                "stt:results" => new[]
                {
                    new StreamEntry("1-0", new (string Name, string Value)[]
                        {
                            ("meeting_id", RoomId),
                            ("segment_id", Guid.NewGuid().ToString()),
                            ("speaker_id", Guid.NewGuid().ToString()),
                            ("language", "en"),
                            ("start_ms", "0"),
                            ("end_ms", "1200"),
                        }
                        .Concat(_sttFields)
                        .Select(f => new NameValueEntry(f.Name, f.Value))
                        .ToArray()),
                },
                "transcript:clean" => new[]
                {
                    Entry(
                        ("meeting_id", RoomId),
                        ("sentence_id", Guid.NewGuid().ToString()),
                        ("revision", "2"),
                        ("speaker_id", Guid.NewGuid().ToString()),
                        ("segment_ids", JsonSerializer.Serialize(new[] { Guid.NewGuid().ToString() })),
                        ("clean_text", "That was a damn good call."),
                        ("language", "en"),
                        ("flags", ""),
                        ("source", "llm"),
                        ("timestamp_ms", "1785575730000")),
                },
                _ => Array.Empty<StreamEntry>(),
            };
        }

        /// <summary>Runs the service until <paramref name="clientMethod"/> has been sent once and
        /// returns its payload.</summary>
        public async Task<T> FirstPayloadAsync<T>(string clientMethod)
        {
            await _service.StartAsync(CancellationToken.None);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var invocation = _proxy.Invocations.FirstOrDefault(i =>
                    i.Method.Name == nameof(IClientProxy.SendCoreAsync) && (string)i.Arguments[0] == clientMethod);
                if (invocation is not null)
                    return Assert.IsType<T>(((object?[])invocation.Arguments[1])[0]);
                await Task.Delay(10);
            }

            throw new TimeoutException($"{clientMethod} was never sent.");
        }

        public async ValueTask DisposeAsync() => await _service.StopAsync(CancellationToken.None);
    }

    private sealed class FakeRoomClient : TranslationRoomService.TranslationRoomServiceClient
    {
        public override AsyncUnaryCall<GetTranslationRoomResponse> GetTranslationRoomByIdAsync(
            GetTranslationRoomRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            Call(new GetTranslationRoomResponse
            {
                Id = request.Id,
                WorkspaceId = WorkspaceId,
                HostId = Guid.NewGuid().ToString(),
                Status = "IN_PROGRESS",
            });

        internal static AsyncUnaryCall<T> Call<T>(T value) => new(
            Task.FromResult(value),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });
    }

    private sealed class FakeWorkspaceClient : WorkspaceService.WorkspaceServiceClient
    {
        private readonly bool _profanityFilter;

        public FakeWorkspaceClient(bool profanityFilter) => _profanityFilter = profanityFilter;

        public override AsyncUnaryCall<GetWorkspaceSettingsResponse> GetWorkspaceSettingsAsync(
            GetWorkspaceSettingsRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            FakeRoomClient.Call(new GetWorkspaceSettingsResponse
            {
                IsProfanityFilterEnabled = _profanityFilter,
                AllowExternalLlm = false,
            });
    }
}
