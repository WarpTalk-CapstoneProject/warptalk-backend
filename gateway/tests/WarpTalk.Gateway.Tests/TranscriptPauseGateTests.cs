using Grpc.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Gateway.Services;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// WT-605: Pause Transcript does not reach the caption lane.
/// </summary>
/// <remarks>
/// Pause Transcript stops the transcript being WRITTEN DOWN. It does not stop the meeting:
/// translation, dubbing and live captions run through a pause.
///
/// This consumer group is the caption lane. It once dropped every stt:results entry while the room
/// was paused, on the theory that captions only come from translate:results. They do not: before
/// Start Translation, or when nobody needs a translation, the STT broadcast is the only thing the
/// caption overlay is made of, so captions froze on the last sentence said before the pause. What
/// keeps paused speech out of the record lives elsewhere — TranscriptService's persistence consumer
/// skips it, and the web client keeps it out of its transcript store.
///
/// These drive the real BackgroundService over a mocked Redis whose pause key IS set, so a gate
/// reintroduced anywhere in the loop fails here rather than in a meeting.
/// </remarks>
public sealed class TranscriptPauseGateTests
{
    private const string RoomId = "8f14e45f-ce0a-4a4f-9f5b-2c3d4e5f6a7b";
    private const string WorkspaceId = "1b3c5d7e-9f01-4234-8567-89abcdef0123";

    [Fact]
    public async Task WhilePaused_CaptionsAndTranslationBothStillFlow()
    {
        await using var harness = new Harness(paused: true);

        await harness.RunUntilAsync(h =>
            h.Sent("TranscriptSegmentReceived") == 1 && h.Sent("TranslationTextReceived") == 1);

        Assert.Equal(1, harness.Sent("TranscriptSegmentReceived"));
        Assert.Equal(1, harness.Sent("TranslationTextReceived"));
    }

    [Fact]
    public async Task WhileNotPaused_TranscriptIsDelivered()
    {
        await using var harness = new Harness(paused: false);

        await harness.RunUntilAsync(h => h.Sent("TranscriptSegmentReceived") == 1);

        Assert.Equal(1, harness.Sent("TranscriptSegmentReceived"));
    }

    /// <summary>
    /// The key TranscriptService writes and warptalk-ai's workers read. A rename does not break
    /// either of them loudly — it silently turns their gates off — so the exact string is pinned.
    /// </summary>
    [Fact]
    public void TheKeyNameIsTheCrossRepoContract()
    {
        Assert.Equal(
            $"translationRoom:{RoomId}:transcript_paused",
            TranscriptPauseKey.For(RoomId));
    }

    // ── Harness ──────────────────────────────────────────────

    /// <summary>
    /// The real AiResultConsumerService over a mocked Redis: one stt:results entry and one
    /// translate:results entry, delivered once, then nothing.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly AiResultConsumerService _service;
        private readonly Mock<IDatabase> _database = new();
        private readonly Mock<IHubClients> _clients = new();
        private readonly Mock<IClientProxy> _proxy = new();
        private readonly Dictionary<string, int> _reads = new();

        public Harness(bool paused)
        {
            var redis = new Mock<IConnectionMultiplexer>();
            redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_database.Object);

            _database
                .Setup(d => d.StreamCreateConsumerGroupAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(),
                    It.IsAny<bool>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(true);

            // Each stream yields its one entry on the first read and nothing afterwards, so the
            // loops keep spinning harmlessly instead of redelivering the same segment forever.
            // The eight-parameter overload, which is the one RedisStreamService.ConsumeAsync's call
            // actually binds to — the shorter ones are still on IDatabase and a setup on either of
            // them matches nothing, silently, and every loop reads an empty batch forever.
            _database
                .Setup(d => d.StreamReadGroupAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
                    It.IsAny<RedisValue?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                    It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, RedisValue _, RedisValue __, RedisValue? ___, int? ____,
                          bool _____, TimeSpan? ______, CommandFlags _______) =>
                    Task.FromResult(NextBatch(key.ToString())));

            // The pause key is set for a paused room exactly as TranscriptService sets it. Nothing in
            // this service may read it; if something starts to, the paused test above fails.
            _database
                .Setup(d => d.KeyExistsAsync(
                    It.Is<RedisKey>(k => k.ToString() == TranscriptPauseKey.For(RoomId)),
                    It.IsAny<CommandFlags>()))
                .ReturnsAsync(paused);
            _database
                .Setup(d => d.StringGetAsync(
                    It.Is<RedisKey>(k => k.ToString() == TranscriptPauseKey.For(RoomId)),
                    It.IsAny<CommandFlags>()))
                .ReturnsAsync(paused ? (RedisValue)TranscriptPauseKey.Payload(DateTime.UtcNow) : RedisValue.Null);

            _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_proxy.Object);
            var hubContext = new Mock<IHubContext<TranslationRoomHub>>();
            hubContext.Setup(c => c.Clients).Returns(_clients.Object);

            _service = new AiResultConsumerService(
                new RedisStreamService(redis.Object, NullLogger<RedisStreamService>.Instance, new ConfigurationBuilder().Build()),
                new ActiveTranslationRoomRegistry(),
                hubContext.Object,
                new FakeWorkspaceClient(),
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
                    Entry("1-0",
                        ("meeting_id", RoomId),
                        ("segment_id", Guid.NewGuid().ToString()),
                        ("speaker_id", Guid.NewGuid().ToString()),
                        ("text", "This sentence is a caption even while paused."),
                        ("language", "en"),
                        ("start_ms", "0"),
                        ("end_ms", "1200")),
                },
                "translate:results" => new[]
                {
                    Entry("2-0",
                        ("meeting_id", RoomId),
                        ("segment_id", Guid.NewGuid().ToString()),
                        ("speaker_id", Guid.NewGuid().ToString()),
                        ("original_text", "This one must keep flowing."),
                        ("translated_text", "Câu này vẫn phải chảy."),
                        ("source_lang", "en"),
                        ("target_lang", "vi")),
                },
                _ => Array.Empty<StreamEntry>(),
            };
        }

        private static StreamEntry Entry(string id, params (string Name, string Value)[] fields) =>
            new(id, fields.Select(f => new NameValueEntry(f.Name, f.Value)).ToArray());

        public int Sent(string clientMethod) =>
            _proxy.Invocations.Count(i =>
                i.Method.Name == nameof(IClientProxy.SendCoreAsync) && (string)i.Arguments[0] == clientMethod);

        /// <summary>
        /// Runs the service until the condition holds, then a beat longer.
        ///
        /// The extra beat is what makes an "it did NOT happen" assertion mean anything: stopping
        /// the instant the translation arrives would pass even if the transcript broadcast were
        /// merely a few milliseconds behind it.
        /// </summary>
        public async Task RunUntilAsync(Func<Harness, bool> until)
        {
            await _service.StartAsync(CancellationToken.None);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && !until(this))
                await Task.Delay(10);

            await Task.Delay(150);
        }

        public async ValueTask DisposeAsync() => await _service.StopAsync(CancellationToken.None);
    }

    /// <summary>Stand-in for the generated gRPC client — same approach as RoomHostAuthorityTests.</summary>
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
        public override AsyncUnaryCall<GetWorkspaceSettingsResponse> GetWorkspaceSettingsAsync(
            GetWorkspaceSettingsRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            FakeRoomClient.Call(new GetWorkspaceSettingsResponse
            {
                IsProfanityFilterEnabled = false,
                AllowExternalLlm = false,
            });
    }
}
