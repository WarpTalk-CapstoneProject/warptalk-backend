using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using WarpTalk.Shared.Protos;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using WarpTalk.TranscriptService.Infrastructure.Redis;
using Xunit;

namespace WarpTalk.TranscriptService.Tests.Infrastructure;

/// <summary>
/// WT-473 / WT-655. The write of <c>transcripts.timeline_anchor_at</c> — the wall-clock instant
/// every <c>TranscriptSegment.StartTimeMs</c> in a meeting is measured from, and therefore the
/// only thing that lets "click a transcript line, seek the recording" mean anything.
///
/// It had no test at all, which is how it shipped dead: the column was written correctly here
/// while the Python producer never actually put <c>anchor_ms</c> on the wire, so every transcript
/// in the system kept a NULL anchor and nothing failed. These cases pin both halves of the
/// contract — that a stated anchor lands, and that a LATER one never moves it.
///
/// Set-once is the load-bearing half. The offsets already stored were measured against the first
/// anchor, so a second write would silently re-time an entire transcript rather than fail; there
/// is no error to notice. Same reasoning as the SET NX stt_worker uses for the same value in
/// Redis, and as <see cref="Transcript.TimelineAnchorAt"/>'s own doc comment.
///
/// Exercised through the private ProcessSttMessageAsync by reflection, matching
/// TranscriptRedisConsumerServicePauseGateTests / TranscriptReadAccessTests — this consumer has
/// no public seam, and asserting on a re-implementation of the parse would pin the test's own
/// arithmetic rather than the consumer's.
/// </summary>
public class TranscriptTimelineAnchorWriteTests
{
    private const string SttStream = "stt:results";

    private static readonly Guid RoomId = Guid.NewGuid();

    /// <summary>2026-08-01T09:15:30.123Z — deliberately carries sub-second precision, since the
    /// whole point of the column is aligning against a recording's own clock.</summary>
    private const long FirstAnchorMs = 1_785_575_730_123L;

    /// <summary>Ten minutes after the first: what a mid-meeting reconnect of the STT worker
    /// publishes, and exactly the value that must NOT win.</summary>
    private const long LaterAnchorMs = FirstAnchorMs + 600_000L;

    [Fact]
    public async Task APositiveAnchor_IsStamped_AsThatExactUtcInstant()
    {
        var fixture = new AnchorFixture();

        var handled = await fixture.ProcessSttAsync(anchorMs: FirstAnchorMs.ToString());

        Assert.True(handled);
        Assert.Single(fixture.AddedSegments);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(FirstAnchorMs).UtcDateTime,
            fixture.Transcript.TimelineAnchorAt);
        Assert.Equal(DateTimeKind.Utc, fixture.Transcript.TimelineAnchorAt!.Value.Kind);

        // The tracked write has to actually be flagged for EF, or the column stays NULL in the
        // database however right the in-memory object looks.
        fixture.Transcripts.Received(1).Update(fixture.Transcript);
    }

    /// <summary>
    /// The one this whole file exists for. A second STT message stating a DIFFERENT anchor must
    /// leave the stored value alone.
    /// </summary>
    [Fact]
    public async Task ALaterAnchor_DoesNotOverwriteTheFirst()
    {
        var fixture = new AnchorFixture();

        Assert.True(await fixture.ProcessSttAsync(anchorMs: FirstAnchorMs.ToString()));
        var afterFirst = fixture.Transcript.TimelineAnchorAt;

        // A DIFFERENT segment id on purpose. Reusing the first one would make the idempotency
        // check ("have I already stored this segment?") skip the whole block, and the test would
        // pass without the set-once guard ever being evaluated — it would be pinning dedup, not
        // first-write-wins.
        Assert.True(await fixture.ProcessSttAsync(
            anchorMs: LaterAnchorMs.ToString(), segmentId: Guid.NewGuid()));

        // Both messages really did run the persistence block. Without this the test would pass
        // just as happily if the second message had been dropped somewhere upstream of the anchor
        // guard, which is the one way "the value did not change" means nothing.
        Assert.Equal(2, fixture.AddedSegments.Count);

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(FirstAnchorMs).UtcDateTime, afterFirst);
        Assert.Equal(afterFirst, fixture.Transcript.TimelineAnchorAt);
        Assert.NotEqual(
            DateTimeOffset.FromUnixTimeMilliseconds(LaterAnchorMs).UtcDateTime,
            fixture.Transcript.TimelineAnchorAt);

        // Once across both messages: the second must not even queue a write.
        fixture.Transcripts.Received(1).Update(fixture.Transcript);
    }

    /// <summary>
    /// What every message on the wire looked like before the producer was fixed. Absent is not
    /// "epoch", it is "this worker never told us" — and the column has to stay NULL so the
    /// transcript reads as un-alignable instead of claiming a 1970 origin.
    /// </summary>
    [Fact]
    public async Task NoAnchorField_LeavesTheColumnNull()
    {
        var fixture = new AnchorFixture();

        var handled = await fixture.ProcessSttAsync(anchorMs: null);

        Assert.True(handled);
        // The segment itself was still stored — the null column is the anchor decision, not a
        // dropped message.
        Assert.Single(fixture.AddedSegments);
        Assert.Null(fixture.Transcript.TimelineAnchorAt);
        fixture.Transcripts.DidNotReceive().Update(Arg.Any<Transcript>());
    }

    /// <summary>0 is the producer's explicit "not stated" sentinel, not a real epoch instant.</summary>
    [Fact]
    public async Task AnchorZero_IsTreatedAsNotStated()
    {
        var fixture = new AnchorFixture();

        var handled = await fixture.ProcessSttAsync(anchorMs: "0");

        Assert.True(handled);
        // The segment itself was still stored — the null column is the anchor decision, not a
        // dropped message.
        Assert.Single(fixture.AddedSegments);
        Assert.Null(fixture.Transcript.TimelineAnchorAt);
        fixture.Transcripts.DidNotReceive().Update(Arg.Any<Transcript>());
    }

    /// <summary>
    /// A garbage anchor must degrade to "not stated" rather than throw — throwing here returns
    /// false, which redelivers and eventually dead-letters a message whose transcript text is
    /// perfectly good. Losing a line of a meeting over an unparsable timestamp is the worse trade.
    /// </summary>
    [Theory]
    [InlineData("not-a-number")]
    [InlineData("")]
    [InlineData("-1")]
    public async Task ANonNumericOrNegativeAnchor_IsTreatedAsNotStated(string anchorMs)
    {
        var fixture = new AnchorFixture();

        var handled = await fixture.ProcessSttAsync(anchorMs: anchorMs);

        Assert.True(handled);
        // The segment itself was still stored — the null column is the anchor decision, not a
        // dropped message.
        Assert.Single(fixture.AddedSegments);
        Assert.Null(fixture.Transcript.TimelineAnchorAt);
        fixture.Transcripts.DidNotReceive().Update(Arg.Any<Transcript>());
    }

    /// <summary>
    /// One room with one already-current transcript, wired so the consumer's real code path runs:
    /// the head-pointer lookup, the per-segment idempotency check and the atomic counter advance
    /// all behave the way the database would.
    /// </summary>
    private sealed class AnchorFixture
    {
        private readonly TranscriptRedisConsumerServiceHarness _harness;

        public AnchorFixture()
        {
            Transcript = new Transcript
            {
                Id = Guid.NewGuid(),
                TranslationRoomId = RoomId,
                WorkspaceId = Guid.NewGuid(),
                Status = "IN_PROGRESS",
                SourceLanguage = "vi",
                IsActive = true,
                IsCurrent = true,
                CreatedAt = DateTime.UtcNow,
                TimelineAnchorAt = null,
            };

            Transcripts = Substitute.For<ITranscriptRepository>();

            // The predicate is COMPILED and applied against the in-memory rows, so the fake
            // answers the question the consumer actually asked. A stub that returns the transcript
            // for any predicate is how a real defect stayed invisible elsewhere in this codebase:
            // the lookup here is `TranslationRoomId == roomId && IsCurrent`, and a fake ignoring
            // it would keep passing even if the consumer stopped filtering on IsCurrent and began
            // stamping the anchor onto a superseded transcript.
            var rows = new List<Transcript> { Transcript };
            Transcripts
                .FirstOrDefaultAsync(Arg.Any<Expression<Func<Transcript, bool>>>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(
                    rows.FirstOrDefault(call.Arg<Expression<Func<Transcript, bool>>>().Compile())));
            Transcripts
                .FindAsync(Arg.Any<Expression<Func<Transcript, bool>>>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(
                    rows.Where(call.Arg<Expression<Func<Transcript, bool>>>().Compile())));

            // Same rule for the segment idempotency check — it must answer per id, otherwise the
            // second message in the set-once test could silently take the "already stored" exit
            // and never reach the anchor guard at all.
            var segments = Substitute.For<ITranscriptSegmentRepository>();
            segments.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(
                    AddedSegments.FirstOrDefault(s => s.Id == call.Arg<Guid>())));
            segments
                .When(r => r.AddAsync(Arg.Any<TranscriptSegment>(), Arg.Any<CancellationToken>()))
                .Do(call => AddedSegments.Add(call.Arg<TranscriptSegment>()));

            var unitOfWork = Substitute.For<IUnitOfWork>();
            unitOfWork.Transcripts.Returns(Transcripts);
            unitOfWork.TranscriptSegments.Returns(segments);

            // No open pause window: WT-605's gate would otherwise skip the segment before the
            // anchor is ever looked at. A default substitute repository returns null, which is
            // "not paused" — spelled out here so it is a decision, not an accident.
            var pauseWindows = Substitute.For<ITranscriptPauseWindowRepository>();
            pauseWindows.GetActiveWindowByRoomIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns((TranscriptPauseWindow?)null);
            unitOfWork.TranscriptPauseWindows.Returns(pauseWindows);

            // The atomic UPDATE ... RETURNING that owns last_sequence_order/total_segments/
            // total_duration_ms. Deliberately does NOT touch the tracked entity: the anchor write
            // is the only thing allowed to mark the transcript modified, which is what lets the
            // Update() assertions above mean what they say.
            unitOfWork
                .AdvanceTranscriptForNewSegmentAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(++_sequenceOrder));

            var services = new ServiceCollection();
            services.AddScoped(_ => unitOfWork);
            services.AddScoped<TranslationRoomService.TranslationRoomServiceClient>(
                _ => new FakeRoomClient(Transcript.WorkspaceId));
            services.AddScoped<UserService.UserServiceClient>(_ => new FakeUserClient());
            services.AddScoped<WorkspaceService.WorkspaceServiceClient>(_ => new FakeWorkspaceClient());

            var redis = Substitute.For<IConnectionMultiplexer>();
            redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(Substitute.For<IDatabase>());

            _harness = new TranscriptRedisConsumerServiceHarness(
                new TranscriptRedisConsumerService(
                    redis,
                    NullLogger<TranscriptRedisConsumerService>.Instance,
                    services.BuildServiceProvider()));
        }

        private int _sequenceOrder;

        public Transcript Transcript { get; }

        public ITranscriptRepository Transcripts { get; }

        public List<TranscriptSegment> AddedSegments { get; } = new();

        /// <param name="anchorMs">Null omits the field entirely — an older producer, or any
        /// message written before it existed.</param>
        public Task<bool> ProcessSttAsync(string? anchorMs, Guid? segmentId = null)
        {
            var fields = new List<(string Name, string Value)>
            {
                ("meeting_id", RoomId.ToString()),
                ("segment_id", (segmentId ?? Guid.NewGuid()).ToString()),
                // "system" rather than a participant guid: TryResolveSpeaker short-circuits on it,
                // so nothing here depends on a UserService round trip it does not care about.
                ("speaker_id", "system"),
                ("text", "một câu đã được nghe ra"),
                ("language", "vi"),
                ("start_ms", "0"),
                ("end_ms", "1500"),
                ("is_final_chunk", "0"),
            };

            if (anchorMs is not null)
            {
                fields.Add(("anchor_ms", anchorMs));
            }

            return _harness.ProcessSttMessageAsync(SttStream, Entry(fields));
        }

        private static StreamEntry Entry(IEnumerable<(string Name, string Value)> fields) =>
            new("1-0", fields.Select(f => new NameValueEntry(f.Name, f.Value)).ToArray());
    }

    /// <summary>Reflection seam onto the private handler — the consumer is a BackgroundService
    /// whose only public entry point is a polling loop against a live Redis.</summary>
    private sealed class TranscriptRedisConsumerServiceHarness
    {
        private static readonly MethodInfo ProcessStt =
            typeof(TranscriptRedisConsumerService).GetMethod(
                "ProcessSttMessageAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(
                nameof(TranscriptRedisConsumerService), "ProcessSttMessageAsync");

        private readonly TranscriptRedisConsumerService _service;

        public TranscriptRedisConsumerServiceHarness(TranscriptRedisConsumerService service)
            => _service = service;

        public Task<bool> ProcessSttMessageAsync(string streamKey, StreamEntry message)
            => (Task<bool>)ProcessStt.Invoke(
                _service, new object[] { streamKey, message, CancellationToken.None })!;
    }

    /// <summary>Stand-in for the generated gRPC client, same approach as
    /// TranscriptRecordingServiceTests/TranscriptReadAccessTests.</summary>
    private sealed class FakeRoomClient : TranslationRoomService.TranslationRoomServiceClient
    {
        private readonly Guid _workspaceId;

        public FakeRoomClient(Guid workspaceId) => _workspaceId = workspaceId;

        public override AsyncUnaryCall<GetTranslationRoomResponse> GetTranslationRoomByIdAsync(
            GetTranslationRoomRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default)
            => GrpcCall.Of(new GetTranslationRoomResponse
            {
                Id = request.Id,
                HostId = Guid.NewGuid().ToString(),
                WorkspaceId = _workspaceId.ToString(),
                Title = "Room",
                Status = "IN_PROGRESS",
                // WT-587: this room IS to be written down. Stated explicitly rather than left to
                // proto3's default, which the consumer reads as "older server, persist anyway" —
                // these tests must not lean on that fallback to reach the persistence path.
                SaveTranscript = true,
            });
    }

    /// <summary>Never called: every message here speaks as "system", which resolves without a
    /// user lookup. Registered because the handler resolves the client before it knows that.
    /// </summary>
    private sealed class FakeUserClient : UserService.UserServiceClient
    {
    }

    private sealed class FakeWorkspaceClient : WorkspaceService.WorkspaceServiceClient
    {
        public override AsyncUnaryCall<GetWorkspaceSettingsResponse> GetWorkspaceSettingsAsync(
            GetWorkspaceSettingsRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default)
            => GrpcCall.Of(new GetWorkspaceSettingsResponse { AllowExternalLlm = true });
    }

    private static class GrpcCall
    {
        public static AsyncUnaryCall<T> Of<T>(T value) => new(
            Task.FromResult(value),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });
    }
}
