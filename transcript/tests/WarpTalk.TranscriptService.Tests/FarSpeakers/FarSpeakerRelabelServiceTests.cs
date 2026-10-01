using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.Application.FarSpeakers;
using WarpTalk.TranscriptService.Domain;
using WarpTalk.TranscriptService.Domain.Entities;

namespace WarpTalk.TranscriptService.Tests.FarSpeakers;

public class FarSpeakerRelabelServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Anchor = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid HostId = Guid.NewGuid();
    private static readonly Guid WorkspaceId = Guid.NewGuid();
    private static readonly Guid StandIn = ExternalBridgeConstants.ParticipantUserId;
    private const string MeetUrl = "https://meet.google.com/abc-mnop-xyz";

    private readonly IFarSpeakerRelabelStore _store = Substitute.For<IFarSpeakerRelabelStore>();
    private readonly IBridgeRoomLookup _rooms = Substitute.For<IBridgeRoomLookup>();
    private readonly IMeetTranscriptSource _meet = Substitute.For<IMeetTranscriptSource>();

    private FarSpeakerRelabelService Sut() =>
        new(_store, _rooms, _meet, NullLogger<FarSpeakerRelabelService>.Instance, new FixedTime(Now));

    private static long Ms(int secondsAfterAnchor) =>
        new DateTimeOffset(Anchor).ToUnixTimeMilliseconds() + secondsAfterAnchor * 1000L;

    private static TranscriptSegment Segment(Guid? speaker, int startS, int endS, string text, string name = "Google Meet participants") => new()
    {
        Id = Guid.NewGuid(),
        SpeakerParticipantId = speaker,
        SpeakerName = name,
        OriginalText = text,
        OriginalLanguage = "en",
        StartTimeMs = startS * 1000,
        EndTimeMs = endS * 1000,
    };

    private static FarSpeakerRelabelJob Job() => new()
    {
        TranslationRoomId = RoomId,
        Status = FarSpeakerRelabelJobStatuses.Pending,
        NextAttemptAt = Now,
        CreatedAt = Now.AddHours(-3),
        UpdatedAt = Now.AddHours(-3),
    };

    private void RoomIs(DateTime? endedAt, bool bridge = true, string? url = MeetUrl) =>
        _rooms.GetAsync(RoomId, Arg.Any<CancellationToken>()).Returns((BridgeRoomLookupOutcome.Found,
            new BridgeRoomInfo(RoomId, bridge, url, HostId, HostId, endedAt is null ? "IN_PROGRESS" : "ENDED", Anchor, endedAt, WorkspaceId)));

    private void TranscriptHas(params TranscriptSegment[] segments) =>
        _store.GetStandInTranscriptAsync(RoomId, StandIn, Arg.Any<CancellationToken>())
            .Returns(new StandInTranscript(Guid.NewGuid(), Anchor, segments));

    private void MeetReturns(MeetTranscriptFetch fetch) =>
        _meet.GetEntriesAsync(HostId, WorkspaceId, MeetUrl, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(fetch);

    private static MeetTranscriptFetch Entries(params MeetTranscriptLine[] lines) =>
        new(null, null, 1, 1, 0, false, lines);

    [Fact]
    public void Apply_NamesStandInSegments_AndNeverTouchesRealParticipantsOrHostLabels()
    {
        var realPerson = Segment(Guid.NewGuid(), 0, 4, "good morning everyone thanks for joining", "Nhi");
        var standIn = Segment(StandIn, 5, 9, "happy to be here as always");
        var hostLabelled = Segment(StandIn, 10, 14, "let us start with the roadmap", "Chosen By Host");
        hostLabelled.FarSpeakerKey = "host:1";
        hostLabelled.FarSpeakerSource = FarSpeakerSources.Host;

        var changed = FarSpeakerRelabelService.Apply(
            [realPerson, standIn, hostLabelled],
            [
                new("conferenceRecords/r/participants/alice", "Alice", Ms(0), Ms(4), "good morning everyone thanks for joining"),
                new("conferenceRecords/r/participants/bob", "Bob", Ms(5), Ms(9), "happy to be here as always"),
                new("conferenceRecords/r/participants/carol", "Carol", Ms(10), Ms(14), "let us start with the roadmap"),
            ],
            Ms(0),
            Now);

        Assert.Equal(1, changed);
        Assert.Equal("Nhi", realPerson.SpeakerName);
        Assert.Null(realPerson.FarSpeakerSource);
        Assert.Equal("Bob", standIn.SpeakerName);
        Assert.Equal("conferenceRecords/r/participants/bob", standIn.FarSpeakerKey);
        Assert.Equal(FarSpeakerSources.GoogleTranscript, standIn.FarSpeakerSource);
        Assert.Equal(1f, standIn.FarSpeakerConfidence);
        Assert.Equal(Now, standIn.UpdatedAt);
        Assert.Equal("Chosen By Host", hostLabelled.SpeakerName);
        Assert.Equal(FarSpeakerSources.Host, hostLabelled.FarSpeakerSource);
    }

    [Fact]
    public void Apply_LowOverlapSegmentKeepsItsName()
    {
        var segment = Segment(StandIn, 0, 10, "zzz");

        var changed = FarSpeakerRelabelService.Apply(
            [segment],
            [new("p/alice", "Alice", Ms(11), Ms(30), "aaa")],
            Ms(0),
            Now);

        // Even the fallback scan's largest shift (2 s) leaves 1 s of 10 covered: under 0.3.
        Assert.Equal(0, changed);
        Assert.Equal("Google Meet participants", segment.SpeakerName);
        Assert.Null(segment.FarSpeakerSource);
    }

    [Fact]
    public void Apply_RerunWithTheSameResult_ChangesNothing()
    {
        var segment = Segment(StandIn, 0, 4, "happy to be here as always");
        MeetTranscriptLine[] entries = [new("p/bob", "Bob", Ms(0), Ms(4), "happy to be here as always")];

        Assert.Equal(1, FarSpeakerRelabelService.Apply([segment], entries, Ms(0), Now));
        Assert.Equal(0, FarSpeakerRelabelService.Apply([segment], entries, Ms(0), Now.AddMinutes(1)));
        Assert.Equal(Now, segment.UpdatedAt);
    }

    [Fact]
    public void Apply_TruncatesLongDisplayNamesToTheColumn()
    {
        var segment = Segment(StandIn, 0, 4, "hello there my friends");
        var longName = new string('x', 140);

        FarSpeakerRelabelService.Apply([segment], [new("p/x", longName, Ms(0), Ms(4), "hello there my friends")], Ms(0), Now);

        Assert.Equal(FarSpeakerRelabelService.SpeakerNameMaxLength, segment.SpeakerName.Length);
    }

    [Fact]
    public async Task Process_RoomStillOpen_RechecksLaterWithoutCountingAnAttempt()
    {
        RoomIs(endedAt: null);
        var job = Job();

        await Sut().ProcessAsync(job, CancellationToken.None);

        Assert.Equal(FarSpeakerRelabelJobStatuses.Pending, job.Status);
        Assert.Equal(0, job.Attempts);
        Assert.Equal(Now + FarSpeakerRelabelSchedule.RoomStillOpenRecheck, job.NextAttemptAt);
        await _meet.DidNotReceiveWithAnyArgs().GetEntriesAsync(default, default, default!, default, default, default);
    }

    [Fact]
    public async Task Process_NotABridgeRoom_IsSkipped()
    {
        RoomIs(endedAt: Now.AddHours(-1), bridge: false);
        var job = Job();

        await Sut().ProcessAsync(job, CancellationToken.None);

        Assert.Equal(FarSpeakerRelabelJobStatuses.Skipped, job.Status);
    }

    [Fact]
    public async Task Process_EndedRoomWithEntries_RelabelsAndCompletes()
    {
        RoomIs(endedAt: Now.AddMinutes(-30));
        var segment = Segment(StandIn, 60, 64, "can everybody hear me now");
        TranscriptHas(segment);
        MeetReturns(Entries(new MeetTranscriptLine("p/alice", "Alice", Ms(60), Ms(64), "can everybody hear me now")));
        var job = Job();

        await Sut().ProcessAsync(job, CancellationToken.None);

        Assert.Equal(FarSpeakerRelabelJobStatuses.Done, job.Status);
        Assert.Equal(1, job.SegmentsRelabeled);
        Assert.Equal("Alice", segment.SpeakerName);
        // The window handed to Meet covers the segments with slack on both sides.
        await _meet.Received(1).GetEntriesAsync(
            HostId,
            WorkspaceId,
            MeetUrl,
            Anchor.AddSeconds(60) - FarSpeakerRelabelService.WindowSlack,
            Anchor.AddSeconds(64) + FarSpeakerRelabelService.WindowSlack,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Process_ScopeMissing_RetriesWithBackoff()
    {
        RoomIs(endedAt: Now.AddMinutes(-30));
        TranscriptHas(Segment(StandIn, 0, 4, "hello"));
        MeetReturns(new MeetTranscriptFetch(MeetConferenceErrorCodes.MeetScopeMissing, null, 0, 0, 0, false, []));
        var job = Job();
        job.Attempts = 2;

        await Sut().ProcessAsync(job, CancellationToken.None);

        Assert.Equal(FarSpeakerRelabelJobStatuses.Pending, job.Status);
        Assert.Equal(3, job.Attempts);
        Assert.Equal(MeetConferenceErrorCodes.MeetScopeMissing, job.LastError);
        Assert.Equal(Now + FarSpeakerRelabelSchedule.Backoff(3), job.NextAttemptAt);
    }

    [Fact]
    public async Task Process_TranscriptStillBeingGenerated_Retries()
    {
        RoomIs(endedAt: Now.AddMinutes(-5));
        TranscriptHas(Segment(StandIn, 0, 4, "hello"));
        MeetReturns(new MeetTranscriptFetch(null, null, 1, 0, 1, false, []));
        var job = Job();

        await Sut().ProcessAsync(job, CancellationToken.None);

        Assert.Equal(FarSpeakerRelabelJobStatuses.Pending, job.Status);
        Assert.Equal(1, job.Attempts);
    }

    [Fact]
    public async Task Process_NoTranscriptADayAfterTheEnd_ConcludesNoTranscript()
    {
        RoomIs(endedAt: Now.AddHours(-25));
        TranscriptHas(Segment(StandIn, 0, 4, "hello"));
        MeetReturns(new MeetTranscriptFetch(null, null, 1, 0, 0, false, []));
        var job = Job();

        await Sut().ProcessAsync(job, CancellationToken.None);

        Assert.Equal(FarSpeakerRelabelJobStatuses.NoTranscript, job.Status);
    }

    [Fact]
    public async Task Process_ThirtyDaysAfterTheEnd_GivesUpWithoutCallingGoogle()
    {
        RoomIs(endedAt: Now.AddDays(-31));
        var job = Job();

        await Sut().ProcessAsync(job, CancellationToken.None);

        Assert.Equal(FarSpeakerRelabelJobStatuses.Abandoned, job.Status);
        await _meet.DidNotReceiveWithAnyArgs().GetEntriesAsync(default, default, default!, default, default, default);
    }

    [Fact]
    public async Task Process_TranscriptWithoutTimelineAnchor_IsSkipped()
    {
        RoomIs(endedAt: Now.AddMinutes(-30));
        _store.GetStandInTranscriptAsync(RoomId, StandIn, Arg.Any<CancellationToken>())
            .Returns(new StandInTranscript(Guid.NewGuid(), null, [Segment(StandIn, 0, 4, "hello")]));
        var job = Job();

        await Sut().ProcessAsync(job, CancellationToken.None);

        Assert.Equal(FarSpeakerRelabelJobStatuses.Skipped, job.Status);
    }

    [Fact]
    public async Task Discover_QueuesOneJobPerNewRoom()
    {
        var rooms = new[] { Guid.NewGuid(), Guid.NewGuid() };
        _store.FindUnqueuedBridgeRoomsAsync(StandIn, Now - FarSpeakerRelabelSchedule.GiveUpAfter, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(rooms);

        var queued = await Sut().DiscoverAsync();

        Assert.Equal(2, queued);
        await _store.Received(1).AddJobsAsync(
            Arg.Is<IEnumerable<FarSpeakerRelabelJob>>(jobs => jobs.Select(j => j.TranslationRoomId).SequenceEqual(rooms)
                && jobs.All(j => j.Status == FarSpeakerRelabelJobStatuses.Pending && j.NextAttemptAt == Now)),
            Arg.Any<CancellationToken>());
        await _store.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Live far-speaker names vs the relabel ────────────────

    private static TranscriptSegment LiveNamed(int startS, int endS, string text, string liveName, float confidence, string? savedAs = null)
    {
        var segment = Segment(StandIn, startS, endS, text, savedAs ?? liveName);
        segment.FarSpeakerKey = liveName;
        segment.FarSpeakerSource = "meet_caption";
        segment.FarSpeakerConfidence = confidence;
        return segment;
    }

    [Fact]
    public void Apply_GoogleAttribution_OverridesALiveName()
    {
        var segment = LiveNamed(0, 4, "happy to be here as always", "Alice", 0.9f);

        var changed = FarSpeakerRelabelService.Apply(
            [segment], [new("p/bob", "Bob", Ms(0), Ms(4), "happy to be here as always")], Ms(0), Now);

        Assert.Equal(1, changed);
        Assert.Equal("Bob", segment.SpeakerName);
        Assert.Equal("p/bob", segment.FarSpeakerKey);
        Assert.Equal(FarSpeakerSources.GoogleTranscript, segment.FarSpeakerSource);
    }

    [Fact]
    public void Apply_LowOverlap_KeepsAConfidentLiveName()
    {
        var segment = LiveNamed(0, 10, "zzz", "Alice", 0.8f);

        var changed = FarSpeakerRelabelService.Apply([segment], [new("p/bob", "Bob", Ms(11), Ms(30), "aaa")], Ms(0), Now);

        Assert.Equal(0, changed);
        Assert.Equal("Alice", segment.SpeakerName);
        Assert.Equal("Alice", segment.FarSpeakerKey);
        Assert.Equal("meet_caption", segment.FarSpeakerSource);
    }

    [Fact]
    public void Apply_LowOverlap_DoesNotLockInALiveNameTheThresholdNoLongerBacks()
    {
        // Saved as "Alice" under a lower threshold; at today's 0.6 a 0.5 guess is not shown.
        var segment = LiveNamed(0, 10, "zzz", "Alice", 0.5f, savedAs: "Alice");

        var changed = FarSpeakerRelabelService.Apply([segment], [new("p/bob", "Bob", Ms(11), Ms(30), "aaa")], Ms(0), Now);

        Assert.Equal(1, changed);
        Assert.Equal("Google Meet participants", segment.SpeakerName);
        // The hint itself is evidence and stays stored as sent.
        Assert.Equal("Alice", segment.FarSpeakerKey);
        Assert.Equal(0.5f, segment.FarSpeakerConfidence);
        Assert.Equal(Now, segment.UpdatedAt);
    }

    [Fact]
    public void Apply_LowOverlap_NoLiveHint_FallsBackToGoogleMeetParticipants()
    {
        var segment = Segment(StandIn, 0, 10, "zzz", "Somebody Stale");

        FarSpeakerRelabelService.Apply([segment], [new("p/bob", "Bob", Ms(11), Ms(30), "aaa")], Ms(0), Now);

        Assert.Equal("Google Meet participants", segment.SpeakerName);
    }

    [Fact]
    public void Apply_LowOverlap_UsesTheConfiguredThreshold()
    {
        var segment = LiveNamed(0, 10, "zzz", "Alice", 0.5f, savedAs: "Google Meet participants");

        FarSpeakerRelabelService.Apply([segment], [new("p/bob", "Bob", Ms(11), Ms(30), "aaa")], Ms(0), Now, minConfidence: 0.4);

        Assert.Equal("Alice", segment.SpeakerName);
    }

    [Fact]
    public void Apply_GoogleAttributesAParticipantWithNoName_DoesNotKeepTheLiveName()
    {
        // Google says a specific participant spoke, and its key replaces the live hint — but it has
        // no name for them. Keeping "Alice" would pair her name with somebody else's key.
        var segment = LiveNamed(0, 4, "happy to be here as always", "Alice", 0.9f);

        FarSpeakerRelabelService.Apply([segment], [new("p/phone", "", Ms(0), Ms(4), "happy to be here as always")], Ms(0), Now);

        Assert.Equal("Google Meet participants", segment.SpeakerName);
        Assert.Equal("p/phone", segment.FarSpeakerKey);
    }

    [Fact]
    public void Apply_LowOverlap_NeverTouchesHostLabelsOrRealParticipants()
    {
        var host = LiveNamed(0, 10, "zzz", "Alice", 0.1f, savedAs: "Chosen By Host");
        host.FarSpeakerSource = FarSpeakerSources.Host;
        var real = Segment(Guid.NewGuid(), 0, 10, "zzz", "Nhi");

        var changed = FarSpeakerRelabelService.Apply([host, real], [new("p/bob", "Bob", Ms(11), Ms(30), "aaa")], Ms(0), Now);

        Assert.Equal(0, changed);
        Assert.Equal("Chosen By Host", host.SpeakerName);
        Assert.Equal("Nhi", real.SpeakerName);
    }

    [Fact]
    public async Task Process_PluginNotConnected_RetriesWithBackoff_AndAsksWithTheRoomsWorkspace()
    {
        RoomIs(endedAt: Now.AddMinutes(-30));
        TranscriptHas(Segment(StandIn, 0, 4, "hello"));
        MeetReturns(new MeetTranscriptFetch(MeetConferenceErrorCodes.PluginNotConnected, null, 0, 0, 0, false, []));
        var job = Job();
        job.Attempts = 1;

        await Sut().ProcessAsync(job, CancellationToken.None);

        Assert.Equal(FarSpeakerRelabelJobStatuses.Pending, job.Status);
        Assert.Equal(2, job.Attempts);
        Assert.Equal(MeetConferenceErrorCodes.PluginNotConnected, job.LastError);
        Assert.Equal(Now + FarSpeakerRelabelSchedule.Backoff(2), job.NextAttemptAt);
        await _meet.Received(1).GetEntriesAsync(
            HostId, WorkspaceId, MeetUrl, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Process_PluginStillNotConnectedAtTheThirtyDayHorizon_GivesUp()
    {
        // Ended just inside the horizon, so Google is asked; the retry that follows crosses it.
        RoomIs(endedAt: Now - FarSpeakerRelabelSchedule.GiveUpAfter + TimeSpan.FromMinutes(1));
        TranscriptHas(Segment(StandIn, 0, 4, "hello"));
        MeetReturns(new MeetTranscriptFetch(MeetConferenceErrorCodes.PluginNotConnected, null, 0, 0, 0, false, []));
        var job = Job();

        await Sut().ProcessAsync(job, CancellationToken.None);

        Assert.Equal(FarSpeakerRelabelJobStatuses.Pending, job.Status);

        // The next attempt, after the horizon: abandoned without asking Google again.
        var later = new FarSpeakerRelabelService(
            _store, _rooms, _meet, NullLogger<FarSpeakerRelabelService>.Instance,
            new FixedTime(Now + TimeSpan.FromMinutes(2)));
        await later.ProcessAsync(job, CancellationToken.None);

        Assert.Equal(FarSpeakerRelabelJobStatuses.Abandoned, job.Status);
        Assert.Equal(MeetConferenceErrorCodes.PluginNotConnected, job.LastError);
    }

    [Fact]
    public async Task Process_AppliesTheConfiguredLiveNameThreshold()
    {
        RoomIs(endedAt: Now.AddMinutes(-30));
        var segment = LiveNamed(0, 10, "zzz", "Alice", 0.7f);
        TranscriptHas(segment);
        MeetReturns(Entries(new MeetTranscriptLine("p/bob", "Bob", Ms(11), Ms(30), "aaa")));
        var job = Job();

        var strict = new FarSpeakerRelabelService(
            _store, _rooms, _meet, NullLogger<FarSpeakerRelabelService>.Instance, new FixedTime(Now),
            new FarSpeakerNameOptions(0.75));
        await strict.ProcessAsync(job, CancellationToken.None);

        Assert.Equal(FarSpeakerRelabelJobStatuses.Done, job.Status);
        Assert.Equal("Google Meet participants", segment.SpeakerName);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(6, 64)]
    [InlineData(7, 120)]
    [InlineData(50, 120)]
    public void Backoff_DoublesFromTwoMinutes_AndCapsAtTwoHours(int attempts, int expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), FarSpeakerRelabelSchedule.Backoff(attempts));
    }

    private sealed class FixedTime(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }
}
