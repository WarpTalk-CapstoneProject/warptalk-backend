using WarpTalk.TranscriptService.Application.FarSpeakers;

namespace WarpTalk.TranscriptService.Tests.FarSpeakers;

public class FarSpeakerAlignmentTests
{
    private const long T0 = 1_790_000_000_000; // an arbitrary meeting start, Unix ms

    private static StandInSegment Seg(int n, long startS, long endS, string text) =>
        new(new Guid(n, 0, 0, new byte[8]), T0 + startS * 1000, T0 + endS * 1000, text);

    private static MeetTranscriptLine Entry(string who, long startS, long endS, string text) =>
        new($"conferenceRecords/r1/participants/{who}", who.ToUpperInvariant(), T0 + startS * 1000, T0 + endS * 1000, text);

    // Two unambiguous utterances well away from the segment under test, matched at offset 0.
    private static StandInSegment[] WithAnchors(params StandInSegment[] segments) =>
    [
        .. segments,
        Seg(900, 300, 304, "anchor one says the quarterly numbers look fine"),
        Seg(901, 310, 314, "anchor two asks about the hiring freeze timeline"),
    ];

    private static MeetTranscriptLine[] WithAnchorEntries(params MeetTranscriptLine[] entries) =>
    [
        .. entries,
        Entry("dan", 300, 304, "anchor one says the quarterly numbers look fine"),
        Entry("erin", 310, 314, "anchor two asks about the hiring freeze timeline"),
    ];

    [Fact]
    public void NoEntries_AttributesNothing()
    {
        var result = FarSpeakerAlignment.Align([Seg(1, 0, 5, "hello there everyone")], []);

        Assert.Empty(result.Assignments);
        Assert.Equal(0, result.OffsetMs);
    }

    [Fact]
    public void NoSegments_AttributesNothing()
    {
        var result = FarSpeakerAlignment.Align([], [Entry("alice", 0, 5, "hi")]);

        Assert.Empty(result.Assignments);
    }

    [Fact]
    public void SameClock_EachSegmentGoesToTheEntryItOverlaps()
    {
        var segments = new[]
        {
            Seg(1, 0, 4, "good morning everyone thanks for joining"),
            Seg(2, 5, 9, "happy to be here as always"),
            Seg(3, 10, 14, "let us start with the roadmap"),
        };
        var entries = new[]
        {
            Entry("alice", 0, 4, "Good morning everyone, thanks for joining."),
            Entry("bob", 5, 9, "Happy to be here, as always."),
            Entry("alice", 10, 14, "Let us start with the roadmap."),
        };

        var result = FarSpeakerAlignment.Align(segments, entries);

        Assert.Equal(0, result.OffsetMs);
        Assert.Equal(3, result.AnchorMatches);
        Assert.Collection(
            result.Assignments,
            a => { Assert.Equal("ALICE", a.DisplayName); Assert.Equal(1f, a.Confidence); },
            a => Assert.Equal("BOB", a.DisplayName),
            a => Assert.Equal("ALICE", a.DisplayName));
        Assert.Equal("conferenceRecords/r1/participants/bob", result.Assignments[1].ParticipantKey);
    }

    [Fact]
    public void ShiftedClock_OffsetIsEstimatedFromTextMatches_AndOverlapUsesIt()
    {
        // WarpTalk's segments run 3 s behind Google's. Without the offset, segment 2 would overlap
        // alice's first entry more than bob's and be misattributed.
        var segments = new[]
        {
            Seg(1, 0, 4, "good morning everyone thanks for joining"),
            Seg(2, 4, 8, "happy to be here as always"),
            Seg(3, 8, 12, "let us start with the roadmap today"),
        };
        var entries = new[]
        {
            Entry("alice", 3, 7, "good morning everyone thanks for joining"),
            Entry("bob", 7, 11, "happy to be here as always"),
            Entry("carol", 11, 15, "let us start with the roadmap today"),
        };

        var result = FarSpeakerAlignment.Align(segments, entries);

        Assert.Equal(3000, result.OffsetMs);
        Assert.Equal(3, result.AnchorMatches);
        Assert.Equal(new[] { "ALICE", "BOB", "CAROL" }, result.Assignments.Select(a => a.DisplayName));
        Assert.All(result.Assignments, a => Assert.Equal(1f, a.Confidence));
    }

    [Fact]
    public void OneWrongAnchor_CannotDragTheMedianOffset()
    {
        var segments = new[]
        {
            Seg(1, 0, 4, "first sentence about the budget"),
            Seg(2, 10, 14, "second sentence about the hiring plan"),
            Seg(3, 20, 24, "third sentence about the launch date"),
            // Repeats words of an entry far away — a spurious anchor with a 60 s difference.
            Seg(4, 30, 34, "fourth thing nobody else says"),
        };
        var entries = new[]
        {
            Entry("alice", 2, 6, "first sentence about the budget"),
            Entry("bob", 12, 16, "second sentence about the hiring plan"),
            Entry("alice", 22, 26, "third sentence about the launch date"),
            Entry("bob", 90, 94, "fourth thing nobody else says"),
        };

        var offset = FarSpeakerAlignment.EstimateOffset(segments, entries, out var anchors);

        Assert.Equal(4, anchors);
        Assert.Equal(2000, offset);
    }

    [Fact]
    public void NoTextMatches_FallsBackToTheBestOffsetNearZero()
    {
        // Entries in another language than the STT text: no anchors. The entries sit 1 s later;
        // the scan finds the offset that covers the segments fully.
        var segments = new[]
        {
            Seg(1, 0, 3, "xin chao moi nguoi"),
            Seg(2, 4, 7, "hom nay chung ta hop"),
        };
        var entries = new[]
        {
            Entry("alice", 1, 4, "hello everyone"),
            Entry("bob", 5, 8, "today we meet"),
        };

        var result = FarSpeakerAlignment.Align(segments, entries);

        Assert.Equal(0, result.AnchorMatches);
        Assert.Equal(1000, result.OffsetMs);
        Assert.Equal(new[] { "ALICE", "BOB" }, result.Assignments.Select(a => a.DisplayName));
    }

    [Fact]
    public void NoTextMatches_AndNothingBetterNearby_KeepsZero()
    {
        var segments = new[] { Seg(1, 0, 4, "aaa bbb") };
        var entries = new[] { Entry("alice", 0, 4, "ccc ddd") };

        var result = FarSpeakerAlignment.Align(segments, entries);

        Assert.Equal(0, result.OffsetMs);
        Assert.Single(result.Assignments);
    }

    [Fact]
    public void BoundarySegment_GoesToTheLargerOverlap_WithItsRatioAsConfidence()
    {
        // 10 s segment: 7 s inside alice's entry, 3 s inside bob's. The anchors pin the offset at 0.
        var segments = WithAnchors(Seg(1, 0, 10, "zzz"));
        var entries = WithAnchorEntries(
            Entry("alice", -5, 7, "aaa"),
            Entry("bob", 7, 20, "bbb"));

        var result = FarSpeakerAlignment.Align(segments, entries);

        Assert.Equal(0, result.OffsetMs);
        var assignment = Assert.Single(result.Assignments, a => a.SegmentId == segments[0].Id);
        Assert.Equal("ALICE", assignment.DisplayName);
        Assert.Equal(0.7f, assignment.Confidence);
    }

    [Fact]
    public void SegmentBarelyOverlapping_IsLeftAlone()
    {
        // Only 2 s of a 10 s segment are covered: below the 0.3 threshold.
        var segments = WithAnchors(Seg(1, 0, 10, "zzz"));
        var entries = WithAnchorEntries(Entry("alice", 8, 20, "aaa"));

        var result = FarSpeakerAlignment.Align(segments, entries);

        Assert.Equal(0, result.OffsetMs);
        Assert.DoesNotContain(result.Assignments, a => a.SegmentId == segments[0].Id);
    }

    [Fact]
    public void SegmentInASilentGap_IsLeftAlone()
    {
        var segments = new[] { Seg(1, 100, 104, "zzz") };
        var entries = new[] { Entry("alice", 0, 4, "aaa"), Entry("bob", 200, 204, "bbb") };

        var result = FarSpeakerAlignment.Align(segments, entries);

        Assert.Empty(result.Assignments);
    }

    [Fact]
    public void EqualOverlap_TieGoesToTheEntryWhoseWordsMatch()
    {
        // Two people talking over each other for the whole segment.
        var segments = new[] { Seg(1, 0, 4, "we should ship on friday") };
        var entries = new[]
        {
            Entry("alice", 0, 4, "no I disagree completely"),
            Entry("bob", 0, 4, "we should ship on friday"),
        };

        var result = FarSpeakerAlignment.Align(segments, entries);

        Assert.Equal("BOB", Assert.Single(result.Assignments).DisplayName);
    }

    [Fact]
    public void ZeroLengthSegment_IsAttributedWhenInsideAnEntry()
    {
        var segments = new[] { Seg(1, 2, 2, "ok") };
        var entries = new[] { Entry("alice", 0, 4, "ok") };

        var result = FarSpeakerAlignment.Align(segments, entries);

        Assert.Equal(1f, Assert.Single(result.Assignments).Confidence);
    }

    [Fact]
    public void ManySpeakers_LongerMeeting_AllAttributedWithAConstantSkew()
    {
        var names = new[] { "alice", "bob", "carol", "dan" };
        var segments = new List<StandInSegment>();
        var entries = new List<MeetTranscriptLine>();
        for (var i = 0; i < 40; i++)
        {
            var who = names[i % names.Length];
            var text = $"utterance number {i} spoken by somebody about topic {i * 7}";
            segments.Add(Seg(i + 1, i * 6, i * 6 + 5, text));
            entries.Add(Entry(who, i * 6 - 1, i * 6 + 4, text)); // Google 1 s earlier
        }

        var result = FarSpeakerAlignment.Align(segments, entries);

        Assert.Equal(-1000, result.OffsetMs);
        Assert.Equal(40, result.Assignments.Count);
        for (var i = 0; i < 40; i++)
            Assert.Equal(names[i % names.Length].ToUpperInvariant(), result.Assignments[i].DisplayName);
    }

    [Fact]
    public void RepeatedSentence_IsNotUsedAsAnAnchor()
    {
        // "yes I agree with that" twice within two minutes: ambiguous, so not an anchor.
        var segments = new[] { Seg(1, 0, 3, "yes I agree with that") };
        var entries = new[]
        {
            Entry("alice", 10, 13, "yes I agree with that"),
            Entry("bob", 40, 43, "yes I agree with that"),
        };

        FarSpeakerAlignment.EstimateOffset(segments, entries, out var anchors);

        Assert.Equal(0, anchors);
    }

    [Theory]
    [InlineData("Hello, World!", "hello world", 1.0)]
    [InlineData("Xin chào mọi người", "xin chào mọi người.", 1.0)]
    [InlineData("a b c d", "a b", 0.5)]
    [InlineData("", "anything", 0.0)]
    public void TextSimilarity_IsCaseAndPunctuationBlind_AndPenalisesLengthMismatch(string a, string b, double expected)
    {
        Assert.Equal(expected, FarSpeakerAlignment.TextSimilarity(a, b), 3);
    }
}
