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
/// WT-605, both halves of it, on the lane a tester actually reported.
/// </summary>
/// <remarks>
/// Pause Transcript stops the transcript being WRITTEN DOWN. It does not stop the meeting:
/// translation, dubbing and subtitles run through a pause, so people keep hearing each other in
/// their own language and keep seeing captions. The persistence consumer got that right from the
/// start. This Gateway consumer group knew nothing about pause at all and broadcast every STT
/// segment straight to the room.
///
/// Nothing in this repo could have caught that, because the failing shape is "an event that should
/// have been suppressed while a state was on" — there was no test anywhere asserting the ABSENCE
/// of a broadcast. So these drive the real BackgroundService over a mocked Redis rather than
/// testing a predicate in isolation: a gate that is correct but wired into the wrong loop, or
/// wired in after the send, reads exactly the same in a unit test of the predicate.
///
/// The pair of assertions in <see cref="WhilePaused_TranscriptIsWithheldButTranslationStillFlows"/>
/// is the point. Suppressing both is the failure mode this feature is one careless edit away from,
/// and it would look like a fix.
/// </remarks>
public sealed class TranscriptPauseGateTests
{
    private const string RoomId = "8f14e45f-ce0a-4a4f-9f5b-2c3d4e5f6a7b";
    private const string WorkspaceId = "1b3c5d7e-9f01-4234-8567-89abcdef0123";

    [Fact]
    public async Task WhilePaused_TranscriptIsWithheldButTranslationStillFlows()
    {
        await using var harness = new Harness(paused: true);

        await harness.RunUntilAsync(h => h.Sent("TranslationTextReceived") == 1);

        // The half that was broken: the words must not leave the server.
        Assert.Equal(0, harness.Sent("TranscriptSegmentReceived"));

        // The half that must NOT break while fixing it. Gating this too would turn Pause
        // Transcript into Stop Translation — captions would vanish mid-sentence, and a room full
        // of people who do not share a language would stop being able to follow each other.
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
    /// A withheld entry is still acknowledged.
    /// </summary>
    /// <remarks>
    /// Skipping the ack is the tempting shortcut — the message was not handled, so leave it. But an
    /// unacked entry sits in the consumer group's pending list, and nothing clears it:
    /// TryRestoreConsumerGroupAsync only rebuilds a group that has vanished. A ten-minute pause in
    /// one busy room would leave hundreds of entries pending on a group shared by every room on
    /// this gateway.
    /// </remarks>
    [Fact]
    public async Task AWithheldEntryIsStillAcknowledged()
    {
        await using var harness = new Harness(paused: true);

        await harness.RunUntilAsync(h => h.Acked("stt:results") == 1);

        Assert.Equal(1, harness.Acked("stt:results"));
    }

    /// <summary>
    /// Redis unreachable ⇒ the segment is delivered.
    /// </summary>
    /// <remarks>
    /// Fail-open, matching TranscriptRedisConsumerService.IsRoomTranscriptPausedAsync, which
    /// persists a segment it could not ask about. The agreement is what matters more than either
    /// direction on its own: two lanes failing opposite ways is the reliable recipe for a live
    /// transcript and a saved transcript that disagree about the same meeting, which is a far
    /// harder bug to explain than a pause that briefly did not hold.
    /// </remarks>
    [Fact]
    public async Task WhenRedisCannotBeReached_TheSegmentIsStillDelivered()
    {
        await using var harness = new Harness(paused: true, pauseLookupThrows: true);

        await harness.RunUntilAsync(h => h.Sent("TranscriptSegmentReceived") == 1);

        Assert.Equal(1, harness.Sent("TranscriptSegmentReceived"));
    }

    // ── The cache, and why it is allowed to exist ────────────

    /// <summary>
    /// TranscriptResumed drops the cached answer, so Resume takes effect on the next segment
    /// rather than whenever the cache happened to age out.
    ///
    /// Without the invalidation this fix becomes a symmetric bug: the host presses Resume, the
    /// banner clears for everyone, and the transcript stays empty. A participant has no way to
    /// tell that from a broken feature, and "wait a few seconds" is not something the UI says.
    /// </summary>
    [Fact]
    public async Task Invalidate_TakesEffectImmediately_RatherThanWaitingForTheCacheToExpire()
    {
        var redis = new Mock<IConnectionMultiplexer>();
        var database = new Mock<IDatabase>();
        redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);

        var paused = true;
        database
            .Setup(d => d.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(() => paused);

        var state = new TranscriptPauseState(redis.Object, NullLogger<TranscriptPauseState>.Instance);

        Assert.True(await state.IsPausedAsync(RoomId, CancellationToken.None));

        // The host resumes. The key is gone, but the answer is cached — and the cache is what the
        // relay is there to clear.
        paused = false;
        Assert.True(await state.IsPausedAsync(RoomId, CancellationToken.None));

        state.Invalidate(RoomId);
        Assert.False(await state.IsPausedAsync(RoomId, CancellationToken.None));
    }

    /// <summary>
    /// A failed lookup is not cached. Caching it would pin the room to "recording" for the whole
    /// cache window on the strength of one dropped connection, which is the direction that writes
    /// down words somebody asked not to be written down.
    /// </summary>
    [Fact]
    public async Task AFailedLookupIsNotCached()
    {
        var redis = new Mock<IConnectionMultiplexer>();
        var database = new Mock<IDatabase>();
        redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);

        var failing = true;
        database
            .Setup(d => d.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns(() => failing
                ? Task.FromException<bool>(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"))
                : Task.FromResult(true));

        var state = new TranscriptPauseState(redis.Object, NullLogger<TranscriptPauseState>.Instance);

        Assert.False(await state.IsPausedAsync(RoomId, CancellationToken.None));

        failing = false;
        Assert.True(await state.IsPausedAsync(RoomId, CancellationToken.None));
    }

    /// <summary>
    /// The key this gate reads is the one TranscriptService writes and warptalk-ai's workers read.
    /// A rename does not break any of them — it silently turns all three gates off — so the exact
    /// string is pinned here rather than left to agree by luck.
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

        public Harness(bool paused, bool pauseLookupThrows = false)
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

            if (pauseLookupThrows)
            {
                _database
                    .Setup(d => d.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                    .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
            }
            else
            {
                _database
                    .Setup(d => d.KeyExistsAsync(
                        It.Is<RedisKey>(k => k.ToString() == TranscriptPauseKey.For(RoomId)),
                        It.IsAny<CommandFlags>()))
                    .ReturnsAsync(paused);
            }

            _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_proxy.Object);
            var hubContext = new Mock<IHubContext<TranslationRoomHub>>();
            hubContext.Setup(c => c.Clients).Returns(_clients.Object);

            _service = new AiResultConsumerService(
                new RedisStreamService(redis.Object, NullLogger<RedisStreamService>.Instance, new ConfigurationBuilder().Build()),
                new ActiveTranslationRoomRegistry(),
                hubContext.Object,
                new FakeWorkspaceClient(),
                new FakeRoomClient(),
                new TranscriptPauseState(redis.Object, NullLogger<TranscriptPauseState>.Instance),
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
                        ("text", "This sentence must not leave the server while paused."),
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

        public int Acked(string streamKey) =>
            _database.Invocations.Count(i =>
                i.Method.Name == nameof(IDatabase.StreamAcknowledgeAsync) &&
                ((RedisKey)i.Arguments[0]).ToString() == streamKey);

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
