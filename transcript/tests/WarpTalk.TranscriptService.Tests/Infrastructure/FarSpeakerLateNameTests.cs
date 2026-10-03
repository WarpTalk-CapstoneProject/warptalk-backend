using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.TranscriptService.Application.FarSpeakers;
using WarpTalk.TranscriptService.Domain;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using WarpTalk.TranscriptService.Infrastructure.Redis;
using Xunit;

namespace WarpTalk.TranscriptService.Tests.Infrastructure;

/// <summary>
/// The LATE far-side name on a SAVED bridge stand-in row. stt_worker names a stand-in line when it
/// finalizes it; on the first line after the Meet side changes speaker the captions naming the new
/// speaker have not arrived yet, so the line is written as "Google Meet participants". About a
/// second later stt_worker publishes the name on <c>stt:far_speaker_late</c> with the SAME
/// segment id, and the row takes it — but only a stand-in row that still reads the fallback, that
/// nobody has attributed since, and only under the threshold the line itself was written under.
///
/// The handler is exercised through the private ProcessFarSpeakerLateMessageAsync by reflection,
/// like TranscriptTimelineAnchorWriteTests: this consumer has no public seam.
/// </summary>
public class FarSpeakerLateNameTests
{
    private const string Stream = "stt:far_speaker_late";
    private static readonly Guid StandIn = ExternalBridgeConstants.ParticipantUserId;
    private static readonly Guid RoomId = Guid.NewGuid();

    private static Dictionary<string, string> LateValues(
        Guid segmentId,
        string name = "Alice Nguyen",
        string? confidence = "0.82",
        string? source = "meet_caption")
    {
        var values = new Dictionary<string, string>
        {
            ["type"] = "far_speaker_late",
            ["meeting_id"] = RoomId.ToString(),
            ["segment_id"] = segmentId.ToString(),
            ["far_speaker_name"] = name,
            ["t_ms"] = "1790000000000",
        };
        if (confidence is not null) values["far_speaker_confidence"] = confidence;
        if (source is not null) values["far_speaker_source"] = source;
        return values;
    }

    private static TranscriptSegment Row(
        Guid? speakerId,
        string speakerName = "Google Meet participants",
        string? source = null,
        string? key = null,
        float? confidence = null) => new()
    {
        Id = Guid.NewGuid(),
        TranscriptId = Guid.NewGuid(),
        SpeakerParticipantId = speakerId,
        SpeakerName = speakerName,
        OriginalText = "Can everybody hear me now?",
        OriginalLanguage = "en",
        FarSpeakerKey = key,
        FarSpeakerSource = source,
        FarSpeakerConfidence = confidence,
    };

    // ── Parse ────────────────────────────────────────────────

    [Fact]
    public void Parse_ReadsTheContractFields()
    {
        var segmentId = Guid.NewGuid();

        Assert.True(TranscriptConsumerPollingPolicy.TryParseFarSpeakerLate(
            Stream, LateValues(segmentId, name: "  Alice Nguyen "), out var late));

        Assert.Equal(RoomId, late.RoomId);
        Assert.Equal(segmentId, late.SegmentId);
        Assert.Equal("Alice Nguyen", late.Name);
        Assert.Equal("meet_caption", late.Source);
        Assert.Equal(0.82f, late.Confidence);
    }

    [Fact]
    public void Parse_AMissingConfidence_IsWellFormed_ButUnknown()
    {
        // Not a parse failure: a well-formed entry whose name is simply not to be shown. Refusing
        // it would retry it into the dead-letter stream.
        Assert.True(TranscriptConsumerPollingPolicy.TryParseFarSpeakerLate(
            Stream, LateValues(Guid.NewGuid(), confidence: null), out var late));
        Assert.Null(late.Confidence);
    }

    [Theory]
    [InlineData("segment_id", "not-a-guid")]
    [InlineData("segment_id", "00000000-0000-0000-0000-000000000000")]
    [InlineData("far_speaker_name", "  ")]
    [InlineData("meeting_id", "not-a-room")]
    public void Parse_NoSegmentNoNameOrNoRoom_IsMalformed(string field, string value)
    {
        var values = LateValues(Guid.NewGuid());
        values[field] = value;

        Assert.False(TranscriptConsumerPollingPolicy.TryParseFarSpeakerLate(Stream, values, out _));
    }

    [Fact]
    public void TheStream_IsReadAfterStt_AndDeadLettersUnderItsOwnName()
    {
        var streams = TranscriptConsumerPollingPolicy.InputStreams;

        Assert.Equal(FarSpeakerNames.LateNameStream, TranscriptConsumerPollingPolicy.FarSpeakerLateStream);
        Assert.True(streams.ToList().IndexOf("stt:far_speaker_late") > streams.ToList().IndexOf("stt:results"));
        Assert.Equal(
            "stt:far_speaker_late:transcript-persistence:dead-letter",
            TranscriptConsumerPollingPolicy.DeadLetterStream(Stream));
    }

    // ── The row rule ─────────────────────────────────────────

    [Theory]
    [InlineData("Google Meet participants")]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-00000000b21d")]
    public void AnUnnamedStandInRow_TakesTheLateName(string speakerName)
    {
        Assert.Equal(
            FarSpeakerLateDecision.Apply,
            TranscriptConsumerPollingPolicy.DecideFarSpeakerLate(Row(StandIn, speakerName)));
    }

    [Fact]
    public void AStandInRowWithASubThresholdLiveHint_StillReadsTheFallback_AndTakesTheLateName()
    {
        var row = Row(StandIn, key: "Bob", source: "meet_caption", confidence: 0.3f);

        Assert.Equal(FarSpeakerLateDecision.Apply, TranscriptConsumerPollingPolicy.DecideFarSpeakerLate(row));
    }

    [Fact]
    public void AnAlreadyNamedRow_IsUntouched()
    {
        var row = Row(StandIn, "Bob Tran", key: "Bob Tran", source: "meet_caption", confidence: 0.9f);

        Assert.Equal(FarSpeakerLateDecision.AlreadyNamed, TranscriptConsumerPollingPolicy.DecideFarSpeakerLate(row));
    }

    [Theory]
    [InlineData("google_transcript")]
    [InlineData("host")]
    public void ARowTheRelabelOrTheHostAttributed_IsUntouched_EvenWhenItReadsTheFallback(string source)
    {
        // The relabel names a participant Google could not name "Google Meet participants" — that is
        // its answer, and it outranks any live guess.
        var row = Row(StandIn, source: source);

        Assert.Equal(FarSpeakerLateDecision.AttributedAfterMeeting, TranscriptConsumerPollingPolicy.DecideFarSpeakerLate(row));
    }

    [Fact]
    public void ARealParticipantsRow_IsUntouched()
    {
        Assert.Equal(
            FarSpeakerLateDecision.NotStandIn,
            TranscriptConsumerPollingPolicy.DecideFarSpeakerLate(Row(Guid.NewGuid(), "Google Meet participants")));
        Assert.Equal(
            FarSpeakerLateDecision.NotStandIn,
            TranscriptConsumerPollingPolicy.DecideFarSpeakerLate(Row(null, "System")));
    }

    [Fact]
    public void NoRow_IsNotStoredYet()
    {
        Assert.Equal(FarSpeakerLateDecision.RowNotStoredYet, TranscriptConsumerPollingPolicy.DecideFarSpeakerLate(null));
    }

    // ── The handler ──────────────────────────────────────────

    [Fact]
    public async Task AnUnnamedStandInRow_IsNamed_WithTheHintBehindIt()
    {
        var fixture = new Fixture();
        var row = fixture.Store(Row(StandIn, key: "Bob", source: "meet_caption", confidence: 0.3f));

        Assert.True(await fixture.ProcessAsync(LateValues(row.Id)));

        Assert.Equal("Alice Nguyen", row.SpeakerName);
        // The hint is written with the name: the post-meeting relabel re-derives an unaligned row's
        // name from its STORED hint, and would otherwise put the fallback back.
        Assert.Equal("Alice Nguyen", row.FarSpeakerKey);
        Assert.Equal("meet_caption", row.FarSpeakerSource);
        Assert.Equal(0.82f, row.FarSpeakerConfidence);
        Assert.Equal(1, fixture.GuardedUpdates);
        Assert.Equal("Alice Nguyen", FarSpeakerNames.ResolveLive(row.FarSpeakerKey, row.FarSpeakerConfidence, 0.6));
    }

    [Fact]
    public async Task ARedelivery_ChangesNothingTheSecondTime()
    {
        var fixture = new Fixture();
        var row = fixture.Store(Row(StandIn));

        Assert.True(await fixture.ProcessAsync(LateValues(row.Id)));
        Assert.True(await fixture.ProcessAsync(LateValues(row.Id, name: "Somebody Else")));

        Assert.Equal("Alice Nguyen", row.SpeakerName);
        Assert.Equal(1, fixture.GuardedUpdates);
    }

    [Fact]
    public async Task AnAlreadyNamedRow_IsNeverOverwritten()
    {
        var fixture = new Fixture();
        var row = fixture.Store(Row(StandIn, "Bob Tran", key: "Bob Tran", source: "meet_caption", confidence: 0.9f));

        Assert.True(await fixture.ProcessAsync(LateValues(row.Id)));

        Assert.Equal("Bob Tran", row.SpeakerName);
        Assert.Equal(0, fixture.GuardedUpdates);
    }

    [Fact]
    public async Task ARealParticipantsRow_IsNeverRenamed()
    {
        var fixture = new Fixture();
        var row = fixture.Store(Row(Guid.NewGuid(), "Nhi"));

        Assert.True(await fixture.ProcessAsync(LateValues(row.Id)));

        Assert.Equal("Nhi", row.SpeakerName);
        Assert.Equal(0, fixture.GuardedUpdates);
    }

    [Fact]
    public async Task ARelabelledRow_IsNeverOverwritten()
    {
        var fixture = new Fixture();
        var row = fixture.Store(Row(StandIn, source: FarSpeakerSources.GoogleTranscript, key: "conferenceRecords/a/participants/b"));

        Assert.True(await fixture.ProcessAsync(LateValues(row.Id)));

        Assert.Equal("Google Meet participants", row.SpeakerName);
        Assert.Equal(0, fixture.GuardedUpdates);
    }

    [Fact]
    public async Task ARowThatChangesBetweenReadAndWrite_KeepsTheNewerAnswer()
    {
        // The guarded UPDATE matches nothing (here: a correction named the row in between). That is
        // a success, not a retry — the newer answer stands.
        var fixture = new Fixture { RenameBeforeWrite = "Corrected By Host" };
        var row = fixture.Store(Row(StandIn));

        Assert.True(await fixture.ProcessAsync(LateValues(row.Id)));

        Assert.Equal("Corrected By Host", row.SpeakerName);
    }

    [Theory]
    [InlineData("0.59")]
    [InlineData(null)]
    [InlineData("not-a-number")]
    public async Task UnderTheThreshold_TheEntryIsAcked_AndNoRowIsRead(string? confidence)
    {
        var fixture = new Fixture();
        var row = fixture.Store(Row(StandIn));

        Assert.True(await fixture.ProcessAsync(LateValues(row.Id, confidence: confidence)));

        Assert.Equal("Google Meet participants", row.SpeakerName);
        Assert.Equal(0, fixture.RowReads);
    }

    [Fact]
    public async Task TheConfiguredThreshold_AppliesToTheLateNameToo()
    {
        var fixture = new Fixture(new FarSpeakerNameOptions(0.9));
        var row = fixture.Store(Row(StandIn));

        Assert.True(await fixture.ProcessAsync(LateValues(row.Id, confidence: "0.82")));

        Assert.Equal("Google Meet participants", row.SpeakerName);
    }

    [Fact]
    public async Task ALateNameThatOutrunsItsRow_StaysPending_AndAppliesOnceTheRowLands()
    {
        var fixture = new Fixture();
        var segmentId = Guid.NewGuid();

        // false = not acknowledged: the entry stays pending and is reclaimed and retried by
        // RecoverStaleMessagesAsync, dead-lettered after MaxDeliveryAttempts. No row is created.
        Assert.False(await fixture.ProcessAsync(LateValues(segmentId)));
        Assert.Empty(fixture.Rows);
        Assert.Equal(0, fixture.GuardedUpdates);

        var row = Row(StandIn);
        row.Id = segmentId;
        fixture.Store(row);

        Assert.True(await fixture.ProcessAsync(LateValues(segmentId)));
        Assert.Equal("Alice Nguyen", row.SpeakerName);
    }

    [Fact]
    public void TheRetry_IsBounded()
    {
        Assert.False(TranscriptConsumerPollingPolicy.ShouldDeadLetter(TranscriptConsumerPollingPolicy.MaxDeliveryAttempts - 1));
        Assert.True(TranscriptConsumerPollingPolicy.ShouldDeadLetter(TranscriptConsumerPollingPolicy.MaxDeliveryAttempts));
    }

    [Fact]
    public async Task ASegmentSkippedByPauseTranscript_IsAcked_NotRetried()
    {
        var segmentId = Guid.NewGuid();
        var fixture = new Fixture(pauseSkipped: segmentId);

        Assert.True(await fixture.ProcessAsync(LateValues(segmentId)));
        Assert.Empty(fixture.Rows);
    }

    [Fact]
    public async Task AnEphemeralRoom_IsAcked_WithoutReadingARow()
    {
        var fixture = new Fixture(saveTranscript: false);

        Assert.True(await fixture.ProcessAsync(LateValues(Guid.NewGuid())));
        Assert.Equal(0, fixture.RowReads);
    }

    [Fact]
    public async Task AMalformedEntry_TakesTheBoundedRetryPath()
    {
        var fixture = new Fixture();
        var values = LateValues(Guid.NewGuid());
        values.Remove("segment_id");

        Assert.False(await fixture.ProcessAsync(values));
    }

    /// <summary>
    /// The handler over an in-memory segment table. NameStandInSegmentLateAsync applies the same
    /// WHERE the real UPDATE has, to the in-memory row, so the tests see what the database would.
    /// </summary>
    private sealed class Fixture
    {
        private static readonly MethodInfo Process =
            typeof(TranscriptRedisConsumerService).GetMethod(
                "ProcessFarSpeakerLateMessageAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(nameof(TranscriptRedisConsumerService), "ProcessFarSpeakerLateMessageAsync");

        private readonly TranscriptRedisConsumerService _service;

        public Fixture(FarSpeakerNameOptions? names = null, bool saveTranscript = true, Guid? pauseSkipped = null)
        {
            var segments = Substitute.For<ITranscriptSegmentRepository>();
            segments.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    RowReads++;
                    return Task.FromResult(Rows.FirstOrDefault(r => r.Id == call.Arg<Guid>()));
                });

            var unitOfWork = Substitute.For<IUnitOfWork>();
            unitOfWork.TranscriptSegments.Returns(segments);
            unitOfWork
                .NameStandInSegmentLateAsync(
                    Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<float>(),
                    Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var row = Rows.FirstOrDefault(r => r.Id == call.ArgAt<Guid>(0));
                    if (row is not null && RenameBeforeWrite is not null) row.SpeakerName = RenameBeforeWrite;

                    var unnamed = call.ArgAt<IReadOnlyList<string>>(5);
                    var protectedSources = call.ArgAt<IReadOnlyList<string>>(6);
                    if (row is null
                        || row.SpeakerParticipantId != call.ArgAt<Guid>(1)
                        || !(string.IsNullOrWhiteSpace(row.SpeakerName) || unnamed.Contains(row.SpeakerName.Trim()))
                        || (row.FarSpeakerSource is not null && protectedSources.Contains(row.FarSpeakerSource)))
                    {
                        return Task.FromResult(false);
                    }

                    row.SpeakerName = call.ArgAt<string>(2);
                    row.FarSpeakerKey = call.ArgAt<string>(2);
                    row.FarSpeakerSource = call.ArgAt<string?>(3);
                    row.FarSpeakerConfidence = call.ArgAt<float>(4);
                    GuardedUpdates++;
                    return Task.FromResult(true);
                });

            var services = new ServiceCollection();
            services.AddScoped(_ => unitOfWork);
            services.AddScoped<TranslationRoomService.TranslationRoomServiceClient>(_ => new FakeRoomClient(saveTranscript));
            if (names is not null) services.AddSingleton(names);

            var db = Substitute.For<IDatabase>();
            db.SetContainsAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
                .Returns(call => Task.FromResult(
                    pauseSkipped is { } skipped
                    && call.ArgAt<RedisKey>(0).ToString() == $"translationRoom:{RoomId}:transcript_paused_segments"
                    && call.ArgAt<RedisValue>(1).ToString() == skipped.ToString()));
            var redis = Substitute.For<IConnectionMultiplexer>();
            redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

            _service = new TranscriptRedisConsumerService(
                redis, NullLogger<TranscriptRedisConsumerService>.Instance, services.BuildServiceProvider());
        }

        public List<TranscriptSegment> Rows { get; } = new();

        public int RowReads { get; private set; }

        public int GuardedUpdates { get; private set; }

        /// <summary>Simulates a write landing between the handler's read and its guarded UPDATE.</summary>
        public string? RenameBeforeWrite { get; init; }

        public TranscriptSegment Store(TranscriptSegment row)
        {
            Rows.Add(row);
            return row;
        }

        public Task<bool> ProcessAsync(Dictionary<string, string> values)
        {
            var entry = new StreamEntry("1-0", values.Select(v => new NameValueEntry(v.Key, v.Value)).ToArray());
            return (Task<bool>)Process.Invoke(_service, new object[] { Stream, entry, CancellationToken.None })!;
        }
    }

    private sealed class FakeRoomClient : TranslationRoomService.TranslationRoomServiceClient
    {
        private readonly bool _saveTranscript;

        public FakeRoomClient(bool saveTranscript) => _saveTranscript = saveTranscript;

        public override AsyncUnaryCall<GetTranslationRoomResponse> GetTranslationRoomByIdAsync(
            GetTranslationRoomRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default)
            => new(
                Task.FromResult(new GetTranslationRoomResponse
                {
                    Id = request.Id,
                    WorkspaceId = Guid.NewGuid().ToString(),
                    Status = "IN_PROGRESS",
                    SaveTranscript = _saveTranscript,
                }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
    }
}
