using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.Helpers;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

/// <summary>
/// The format ArtifactsFinalizer hands to warptalk-ai when no summary arrived in time.
/// </summary>
/// <remarks>
/// Worth its own file because every failure here is SILENT. A wrong offset still parses, still
/// looks like a citation, and still renders — it simply points at a moment the meeting does not
/// contain, and nothing anywhere would report that. The Python side is the other half of this
/// contract (<c>format_transcript_line</c> in ai_assistant_worker/summary_templates.py) and is
/// tested against the same shape.
/// </remarks>
public class CitedTranscriptFormatterTests
{
    private static CitedTranscriptFormatter.Segment Segment(int startMs, string speaker, string text) =>
        new(startMs, speaker, text);

    [Fact]
    public void OffsetsAreRelativeToTheFirstSegment_NotAbsolute()
    {
        // A real meeting's stored segments do not start at zero — they start whenever the meeting
        // did. Emitting these verbatim is the failure this test exists for.
        var transcript = CitedTranscriptFormatter.Format(new[]
        {
            Segment(1_200_000, "Tu", "shall we start"),
            Segment(1_290_210, "Nhi", "yes"),
        });

        transcript.Should().Be("[t=0] [Tu] shall we start\n[t=90210] [Nhi] yes");
    }

    [Fact]
    public void TheFormatMatchesTheOneThePythonWorkerProduces()
    {
        // Byte for byte what format_transcript_line emits: "[t={ms}] [{speaker}] {text}".
        CitedTranscriptFormatter.Format(new[] { Segment(0, "Tu", "hello") })
            .Should().Be("[t=0] [Tu] hello");
    }

    [Fact]
    public void SegmentsWithNoWordsAreDropped()
    {
        // WT-478: "[t=0] [Nhi] " is non-empty to code and empty to a reader. A transcript of those
        // reached the model once already and came back reported as a real summary.
        var transcript = CitedTranscriptFormatter.Format(new[]
        {
            Segment(0, "Tu", "hello"),
            Segment(500, "Nhi", "   "),
            Segment(900, "Ky", ""),
            Segment(1_500, "Tuan", "goodbye"),
        });

        transcript.Should().Be("[t=0] [Tu] hello\n[t=1500] [Tuan] goodbye");
    }

    [Fact]
    public void TheBaseIsTakenFromTheSPOKENSegments_NotTheDroppedOnes()
    {
        // A blank segment before the first real one must not become the origin: every offset after
        // it would then be shifted by however long the silence was.
        var transcript = CitedTranscriptFormatter.Format(new[]
        {
            Segment(1_000, "Tu", "  "),
            Segment(5_000, "Nhi", "first words"),
            Segment(6_000, "Ky", "second"),
        });

        transcript.Should().Be("[t=0] [Nhi] first words\n[t=1000] [Ky] second");
    }

    [Fact]
    public void AMeetingNobodySpokeInFormatsAsEmpty()
    {
        // Empty is the signal ArtifactsFinalizer uses to NOT ask for a summary at all — asking
        // would spend a model call to be told the meeting was silent, which it already knows.
        CitedTranscriptFormatter.Format(System.Array.Empty<CitedTranscriptFormatter.Segment>())
            .Should().BeEmpty();

        CitedTranscriptFormatter.Format(new[] { Segment(0, "Tu", "   ") })
            .Should().BeEmpty();
    }

    [Fact]
    public void OutOfOrderSegmentsStillMeasureFromTheEarliest()
    {
        // The finalizer orders by SequenceOrder, but the base has to come from the smallest START
        // TIME — the two are not required to agree, and a negative offset would be nonsense.
        var transcript = CitedTranscriptFormatter.Format(new[]
        {
            Segment(9_000, "Tu", "later"),
            Segment(3_000, "Nhi", "earlier"),
        });

        transcript.Should().Be("[t=6000] [Tu] later\n[t=0] [Nhi] earlier");
    }
}
