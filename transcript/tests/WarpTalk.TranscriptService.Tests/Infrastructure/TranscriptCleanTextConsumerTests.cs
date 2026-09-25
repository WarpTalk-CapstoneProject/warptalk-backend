using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
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
/// WT-716. What the persistence consumer does with a clean transcript — tier 1 (clean_text /
/// clean_flags riding on stt:results) and tier 2 (whole sentences on transcript:clean).
///
/// The raw record is the thing these protect as much as the clean one: cleaning is stored BESIDE
/// original_text and never instead of it, absent must stay distinguishable from "filler only",
/// and a sentence revision must never be overwritten by an older one arriving late.
///
/// Driven through the private handlers by reflection, the same seam
/// TranscriptTimelineAnchorWriteTests uses — this consumer has no public entry point other than a
/// polling loop against a live Redis.
/// </summary>
public class TranscriptCleanTextConsumerTests
{
    private static readonly Guid RoomId = Guid.NewGuid();

    // ── Tier 1: segment clean_text / clean_flags ─────────────

    [Fact]
    public async Task CleanTextAndFlags_AreStoredBesideTheUntouchedRawText()
    {
        var fixture = new Fixture();

        Assert.True(await fixture.ProcessSttAsync(
            text: "ừm thì mình mình chốt thứ hai nhé",
            cleanText: "thì mình chốt thứ hai nhé",
            cleanFlags: "fillers_removed, stutter_removed"));

        var segment = Assert.Single(fixture.AddedSegments);
        Assert.Equal("ừm thì mình mình chốt thứ hai nhé", segment.OriginalText);
        Assert.Equal("thì mình chốt thứ hai nhé", segment.CleanText);
        Assert.Equal(["fillers_removed", "stutter_removed"], Assert.IsType<string[]>(segment.CleanFlags));
    }

    [Fact]
    public async Task AbsentCleanFields_StoreNull_NotAnEmptyCleaning()
    {
        // An stt_worker that predates cleaning. Null is "not cleaned — show the raw text"; storing
        // "" here would make every old-producer line vanish from the Clean view as "filler only".
        var fixture = new Fixture();

        Assert.True(await fixture.ProcessSttAsync(text: "hello there", cleanText: null, cleanFlags: null));

        var segment = Assert.Single(fixture.AddedSegments);
        Assert.Null(segment.CleanText);
        Assert.Null(segment.CleanFlags);
    }

    [Fact]
    public async Task AnEmptyCleanText_IsKeptAsFillerOnly()
    {
        var fixture = new Fixture();

        Assert.True(await fixture.ProcessSttAsync(text: "えーと、あの", cleanText: "", cleanFlags: "filler_only"));

        var segment = Assert.Single(fixture.AddedSegments);
        Assert.Equal("えーと、あの", segment.OriginalText);
        Assert.Equal(string.Empty, segment.CleanText);
        Assert.Equal(["filler_only"], Assert.IsType<string[]>(segment.CleanFlags));
    }

    [Fact]
    public async Task CleanTextWithoutFlags_HasAnEmptyFlagList()
    {
        var fixture = new Fixture();

        Assert.True(await fixture.ProcessSttAsync(text: "all good", cleanText: "all good", cleanFlags: null));

        var segment = Assert.Single(fixture.AddedSegments);
        Assert.Equal("all good", segment.CleanText);
        Assert.NotNull(segment.CleanFlags);
        Assert.Empty(segment.CleanFlags!);
    }

    // ── Tier 2: clean sentences ──────────────────────────────

    [Fact]
    public async Task ANewSentence_IsStoredOnTheTranscriptItsSegmentsBelongTo()
    {
        var fixture = new Fixture();
        var first = fixture.StoreSegment(sequenceOrder: 4, startMs: 9_000);
        var second = fixture.StoreSegment(sequenceOrder: 5, startMs: 10_500);
        var sentenceId = Guid.NewGuid();
        var speaker = Guid.NewGuid();

        Assert.True(await fixture.ProcessCleanSentenceAsync(
            sentenceId, revision: 1, segmentIds: [first.Id, second.Id],
            cleanText: "We ship on Monday.", speakerId: speaker.ToString(),
            flags: "self_repair", source: "llm", language: "en", timestampMs: "1785575730123"));

        var stored = Assert.Single(fixture.Sentences);
        Assert.Equal(sentenceId, stored.Id);
        Assert.Equal(fixture.Transcript.Id, stored.TranscriptId);
        Assert.Equal(speaker, stored.SpeakerParticipantId);
        Assert.Equal([first.Id, second.Id], stored.SegmentIds);
        Assert.Equal("We ship on Monday.", stored.CleanText);
        Assert.Equal("en", stored.Language);
        Assert.Equal(["self_repair"], stored.Flags);
        Assert.Equal("llm", stored.Source);
        Assert.Equal(1, stored.Revision);
        Assert.Equal(9_000, stored.StartTimeMs);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_785_575_730_123L).UtcDateTime, stored.ProducedAt);
        await fixture.UnitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AHigherRevision_ReplacesTheSentence()
    {
        var fixture = new Fixture();
        var segment = fixture.StoreSegment(sequenceOrder: 1, startMs: 0);
        var sentenceId = Guid.NewGuid();

        Assert.True(await fixture.ProcessCleanSentenceAsync(sentenceId, revision: 1, segmentIds: [segment.Id], cleanText: "first draft"));
        Assert.True(await fixture.ProcessCleanSentenceAsync(sentenceId, revision: 2, segmentIds: [segment.Id], cleanText: "second draft", flags: "fallback_raw", source: "prepass"));

        var stored = Assert.Single(fixture.Sentences);
        Assert.Equal(2, stored.Revision);
        Assert.Equal("second draft", stored.CleanText);
        Assert.Equal(["fallback_raw"], stored.Flags);
        Assert.Equal("prepass", stored.Source);
        fixture.SentenceRepository.Received(1).Update(stored);
    }

    [Theory]
    [InlineData(3)] // redelivery of the stored revision
    [InlineData(2)] // an older revision arriving late
    public async Task AnEqualOrLowerRevision_IsAckedWithoutTouchingTheRow(int incomingRevision)
    {
        var fixture = new Fixture();
        var segment = fixture.StoreSegment(sequenceOrder: 1, startMs: 0);
        var sentenceId = Guid.NewGuid();
        Assert.True(await fixture.ProcessCleanSentenceAsync(sentenceId, revision: 3, segmentIds: [segment.Id], cleanText: "the latest"));
        fixture.UnitOfWork.ClearReceivedCalls();

        // TRUE, not false: a revision that can never apply must not be retried into the dead-letter
        // stream and reported as a broken consumer.
        Assert.True(await fixture.ProcessCleanSentenceAsync(sentenceId, revision: incomingRevision, segmentIds: [segment.Id], cleanText: "stale"));

        var stored = Assert.Single(fixture.Sentences);
        Assert.Equal(3, stored.Revision);
        Assert.Equal("the latest", stored.CleanText);
        fixture.SentenceRepository.DidNotReceive().Update(Arg.Any<TranscriptCleanSentence>());
        await fixture.UnitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ASentenceAheadOfItsSegments_IsStoredAnyway_OnTheCurrentTranscript()
    {
        // Tier 2 can finish before the consumer persists the segments it covers. Retrying until
        // they land would only delay it — and dead-letter it on a slow meeting.
        var fixture = new Fixture();

        Assert.True(await fixture.ProcessCleanSentenceAsync(
            Guid.NewGuid(), revision: 0, segmentIds: [Guid.NewGuid(), Guid.NewGuid()], cleanText: "Early bird."));

        var stored = Assert.Single(fixture.Sentences);
        Assert.Equal(fixture.Transcript.Id, stored.TranscriptId);
        Assert.Null(stored.StartTimeMs);
    }

    [Fact]
    public async Task ASentenceOfOnlyUnstoredSegments_IsDropped_WhileTheRoomIsPaused()
    {
        var fixture = new Fixture(paused: true);

        Assert.True(await fixture.ProcessCleanSentenceAsync(
            Guid.NewGuid(), revision: 1, segmentIds: [Guid.NewGuid()], cleanText: "said while paused"));

        Assert.Empty(fixture.Sentences);
    }

    [Fact]
    public async Task ASentenceOfPauseSkippedSegments_IsDropped_EvenAfterResume()
    {
        var skipped = Guid.NewGuid();
        var fixture = new Fixture(paused: false, pauseSkippedSegments: [skipped]);

        Assert.True(await fixture.ProcessCleanSentenceAsync(
            Guid.NewGuid(), revision: 1, segmentIds: [skipped], cleanText: "said while paused"));

        Assert.Empty(fixture.Sentences);
    }

    [Fact]
    public async Task ASentenceStraddlingThePause_IsKept_BecauseItsStoredHalfIsOnTheRecord()
    {
        var fixture = new Fixture(paused: true);
        var stored = fixture.StoreSegment(sequenceOrder: 7, startMs: 1_000);

        Assert.True(await fixture.ProcessCleanSentenceAsync(
            Guid.NewGuid(), revision: 1, segmentIds: [stored.Id, Guid.NewGuid()], cleanText: "half and half"));

        Assert.Single(fixture.Sentences);
    }

    [Fact]
    public async Task AnEphemeralRoom_StoresNoSentence()
    {
        var fixture = new Fixture(saveTranscript: false);
        var segment = fixture.StoreSegment(sequenceOrder: 1, startMs: 0);

        Assert.True(await fixture.ProcessCleanSentenceAsync(Guid.NewGuid(), revision: 1, segmentIds: [segment.Id], cleanText: "not kept"));

        Assert.Empty(fixture.Sentences);
    }

    [Fact]
    public async Task ARoomWithNoTranscriptYet_IsRetried()
    {
        var fixture = new Fixture(hasTranscript: false);

        Assert.False(await fixture.ProcessCleanSentenceAsync(Guid.NewGuid(), revision: 1, segmentIds: [Guid.NewGuid()], cleanText: "too early"));

        Assert.Empty(fixture.Sentences);
    }

    [Fact]
    public async Task AMalformedSentence_TakesTheRetryThenDeadLetterPath()
    {
        var fixture = new Fixture();

        Assert.False(await fixture.ProcessRawCleanSentenceAsync(
            ("meeting_id", RoomId.ToString()),
            ("sentence_id", "not-a-guid"),
            ("revision", "1"),
            ("segment_ids", JsonSerializer.Serialize(new[] { Guid.NewGuid().ToString() })),
            ("clean_text", "x")));

        Assert.Empty(fixture.Sentences);
    }

    // ── Fixture ──────────────────────────────────────────────

    private sealed class Fixture
    {
        private static readonly MethodInfo ProcessStt = PrivateHandler("ProcessSttMessageAsync");
        private static readonly MethodInfo ProcessClean = PrivateHandler("ProcessCleanSentenceMessageAsync");

        private readonly TranscriptRedisConsumerService _service;
        private readonly List<TranscriptSegment> _storedSegments = new();
        private int _sequenceOrder;

        public Fixture(
            bool paused = false,
            bool saveTranscript = true,
            bool hasTranscript = true,
            IReadOnlyCollection<Guid>? pauseSkippedSegments = null)
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
            };
            var transcriptRows = hasTranscript ? new List<Transcript> { Transcript } : new List<Transcript>();

            var transcripts = Substitute.For<ITranscriptRepository>();
            transcripts
                .FirstOrDefaultAsync(Arg.Any<Expression<Func<Transcript, bool>>>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(
                    transcriptRows.FirstOrDefault(call.Arg<Expression<Func<Transcript, bool>>>().Compile())));
            transcripts
                .FindAsync(Arg.Any<Expression<Func<Transcript, bool>>>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(
                    transcriptRows.Where(call.Arg<Expression<Func<Transcript, bool>>>().Compile())));

            // Predicates compiled and applied, so "which of these segment ids are stored" is
            // answered the way the database would answer it.
            var segments = Substitute.For<ITranscriptSegmentRepository>();
            segments.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(_storedSegments.FirstOrDefault(s => s.Id == call.Arg<Guid>())));
            segments
                .FindAsync(Arg.Any<Expression<Func<TranscriptSegment, bool>>>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(
                    _storedSegments.Where(call.Arg<Expression<Func<TranscriptSegment, bool>>>().Compile()).ToList().AsEnumerable()));
            segments
                .When(r => r.AddAsync(Arg.Any<TranscriptSegment>(), Arg.Any<CancellationToken>()))
                .Do(call =>
                {
                    AddedSegments.Add(call.Arg<TranscriptSegment>());
                    _storedSegments.Add(call.Arg<TranscriptSegment>());
                });

            SentenceRepository = Substitute.For<ITranscriptCleanSentenceRepository>();
            SentenceRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(Sentences.FirstOrDefault(s => s.Id == call.Arg<Guid>())));
            SentenceRepository
                .When(r => r.AddAsync(Arg.Any<TranscriptCleanSentence>(), Arg.Any<CancellationToken>()))
                .Do(call => Sentences.Add(call.Arg<TranscriptCleanSentence>()));

            var pauseWindows = Substitute.For<ITranscriptPauseWindowRepository>();
            pauseWindows.GetActiveWindowByRoomIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(paused
                    ? new TranscriptPauseWindow { Id = Guid.NewGuid(), TranslationRoomId = RoomId, StartedAt = DateTime.UtcNow, PausedBy = Guid.NewGuid() }
                    : null);

            UnitOfWork = Substitute.For<IUnitOfWork>();
            UnitOfWork.Transcripts.Returns(transcripts);
            UnitOfWork.TranscriptSegments.Returns(segments);
            UnitOfWork.TranscriptCleanSentences.Returns(SentenceRepository);
            UnitOfWork.TranscriptPauseWindows.Returns(pauseWindows);
            UnitOfWork
                .AdvanceTranscriptForNewSegmentAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(++_sequenceOrder));

            var services = new ServiceCollection();
            services.AddScoped(_ => UnitOfWork);
            services.AddScoped<TranslationRoomService.TranslationRoomServiceClient>(
                _ => new FakeRoomClient(Transcript.WorkspaceId, saveTranscript));
            services.AddScoped<UserService.UserServiceClient>(_ => new FakeUserClient());
            services.AddScoped<WorkspaceService.WorkspaceServiceClient>(_ => new FakeWorkspaceClient());

            var skipped = new HashSet<string>((pauseSkippedSegments ?? Array.Empty<Guid>()).Select(id => id.ToString()));
            var database = Substitute.For<IDatabase>();
            database.SetContainsAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>())
                .Returns(call => Task.FromResult(
                    (string)call.Arg<RedisKey>()! == $"translationRoom:{RoomId}:transcript_paused_segments"
                    && skipped.Contains((string)call.Arg<RedisValue>()!)));
            var redis = Substitute.For<IConnectionMultiplexer>();
            redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);

            _service = new TranscriptRedisConsumerService(
                redis,
                NullLogger<TranscriptRedisConsumerService>.Instance,
                services.BuildServiceProvider());
        }

        public Transcript Transcript { get; }

        public IUnitOfWork UnitOfWork { get; }

        public ITranscriptCleanSentenceRepository SentenceRepository { get; }

        public List<TranscriptSegment> AddedSegments { get; } = new();

        public List<TranscriptCleanSentence> Sentences { get; } = new();

        public TranscriptSegment StoreSegment(int sequenceOrder, int startMs)
        {
            var segment = new TranscriptSegment
            {
                Id = Guid.NewGuid(),
                TranscriptId = Transcript.Id,
                SpeakerName = "System",
                OriginalText = "raw",
                OriginalLanguage = "en",
                SequenceOrder = sequenceOrder,
                StartTimeMs = startMs,
                EndTimeMs = startMs + 1_000,
            };
            _storedSegments.Add(segment);
            return segment;
        }

        /// <param name="cleanText">Null omits the field.</param>
        /// <param name="cleanFlags">Null omits the field.</param>
        public Task<bool> ProcessSttAsync(string text, string? cleanText, string? cleanFlags)
        {
            var fields = new List<(string Name, string Value)>
            {
                ("meeting_id", RoomId.ToString()),
                ("segment_id", Guid.NewGuid().ToString()),
                ("speaker_id", "system"),
                ("text", text),
                ("language", "vi"),
                ("start_ms", "0"),
                ("end_ms", "1500"),
                ("is_final_chunk", "0"),
            };
            if (cleanText is not null) fields.Add(("clean_text", cleanText));
            if (cleanFlags is not null) fields.Add(("clean_flags", cleanFlags));

            return Invoke(ProcessStt, "stt:results", fields);
        }

        public Task<bool> ProcessCleanSentenceAsync(
            Guid sentenceId,
            int revision,
            IReadOnlyList<Guid> segmentIds,
            string cleanText,
            string speakerId = "system",
            string flags = "",
            string source = "llm",
            string language = "vi",
            string timestampMs = "1785575730000")
            => ProcessRawCleanSentenceAsync(
                ("meeting_id", RoomId.ToString()),
                ("sentence_id", sentenceId.ToString()),
                ("revision", revision.ToString()),
                ("speaker_id", speakerId),
                ("segment_ids", JsonSerializer.Serialize(segmentIds.Select(id => id.ToString()))),
                ("clean_text", cleanText),
                ("language", language),
                ("flags", flags),
                ("source", source),
                ("timestamp_ms", timestampMs));

        public Task<bool> ProcessRawCleanSentenceAsync(params (string Name, string Value)[] fields)
            => Invoke(ProcessClean, "transcript:clean", fields);

        private Task<bool> Invoke(MethodInfo handler, string stream, IEnumerable<(string Name, string Value)> fields)
        {
            var entry = new StreamEntry("1-0", fields.Select(f => new NameValueEntry(f.Name, f.Value)).ToArray());
            return (Task<bool>)handler.Invoke(_service, new object[] { stream, entry, CancellationToken.None })!;
        }

        private static MethodInfo PrivateHandler(string name) =>
            typeof(TranscriptRedisConsumerService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(nameof(TranscriptRedisConsumerService), name);
    }

    private sealed class FakeRoomClient : TranslationRoomService.TranslationRoomServiceClient
    {
        private readonly Guid _workspaceId;
        private readonly bool _saveTranscript;

        public FakeRoomClient(Guid workspaceId, bool saveTranscript)
        {
            _workspaceId = workspaceId;
            _saveTranscript = saveTranscript;
        }

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
                SaveTranscript = _saveTranscript,
            });
    }

    /// <summary>Never called: every segment here speaks as "system".</summary>
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
