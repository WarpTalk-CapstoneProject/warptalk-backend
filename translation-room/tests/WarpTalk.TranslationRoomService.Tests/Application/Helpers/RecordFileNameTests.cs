using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.Helpers;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

/// <summary>
/// What a downloaded transcript, summary or recording is called.
///
/// The thing being replaced is "warptalk-optional_recording-3f2a…b7.mp4" — a primary key with a
/// hyphen in it. These tests are about a person finding the right file in their Downloads folder
/// six months later, and about an operating system accepting it in the first place.
/// </summary>
public class RecordFileNameTests
{
    private static readonly DateTime Started = new(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheMeetingLeadsTheKindFollowsAndTheDateTrails()
    {
        RecordFileName.For("Họp sprint 42", RecordFileName.Transcript, Started, null, "txt")
            .Should().Be("Họp sprint 42 - Transcript - 2026-09-18.txt");
    }

    [Fact]
    public void DiacriticsSurvive()
    {
        // Stripping them silently corrects the host's own words. The download carries UTF-8 fine —
        // see ContentDispositionHeader, which sends the real name and an ASCII fallback beside it.
        RecordFileName.For("Đánh giá quý", RecordFileName.Summary, Started, null, "txt")
            .Should().StartWith("Đánh giá quý - Summary");
    }

    [Fact]
    public void CharactersAnOperatingSystemRefusesAreRemoved()
    {
        // A title is user text. Somebody will put a slash in one, and a download that fails is
        // worse than a name with a space where the slash was.
        var name = RecordFileName.For("Q3/Q4: kế hoạch <draft>?", RecordFileName.Recording, Started, null, "mp4");

        name.IndexOfAny(Path.GetInvalidFileNameChars()).Should().Be(-1);
        name.Should().Contain("kế hoạch");
        name.Should().EndWith(" - Recording - 2026-09-18.mp4");
    }

    [Fact]
    public void ATitleSpanningLinesBecomesOneLineRatherThanARunOfBlanks()
    {
        RecordFileName.For("Họp sprint\n\nvà kế hoạch", RecordFileName.Transcript, null, null, "txt")
            .Should().Be("Họp sprint và kế hoạch - Transcript.txt");
    }

    [Fact]
    public void AVeryLongTitleStopsAtEightyCharactersAndSaysSo()
    {
        var name = RecordFileName.For(new string('a', 200), RecordFileName.Transcript, null, null, "txt");

        // The same limit and the same ellipsis the minutes have always used: the shared
        // DocumentFileName decides, so a meeting's minutes and its transcript cannot disagree
        // about where its title ends.
        name.Should().Be(new string('a', 80) + "… - Transcript.txt");
    }

    [Fact]
    public void ALongTitleIsCutAtAWordWhereThereIsOne()
    {
        var title = string.Join(" ", Enumerable.Repeat("kế hoạch", 40));

        var name = RecordFileName.For(title, RecordFileName.Transcript, null, null, "txt");

        name.Should().Contain("… - Transcript.txt");
        name.Should().NotContain("kế hoạc…");
    }

    [Fact]
    public void ATranslatedReadingCarriesItsLanguage()
    {
        RecordFileName.For("Sync", RecordFileName.Summary, null, "ja", "txt")
            .Should().Be("Sync - Summary (JA).txt");
    }

    [Fact]
    public void NoLanguageMeansNoBrackets()
    {
        // A transcript is in whatever was spoken, which is not one thing; a blank must not arrive
        // as "Transcript ()".
        RecordFileName.For("Sync", RecordFileName.Transcript, null, "  ", "txt")
            .Should().Be("Sync - Transcript.txt");
    }

    [Fact]
    public void AnUntitledMeetingStillGetsANameTheSystemCanWrite()
    {
        RecordFileName.For(null, RecordFileName.Transcript, null, null, "txt")
            .Should().Be("Meeting - Transcript.txt");
        RecordFileName.For("   ", RecordFileName.Transcript, null, null, "txt")
            .Should().Be("Meeting - Transcript.txt");
        // A title made entirely of refused characters cleans down to nothing, which is the same
        // situation arriving by a different road.
        RecordFileName.For("///", RecordFileName.Transcript, null, null, "txt")
            .Should().Be("Meeting - Transcript.txt");
    }

    [Fact]
    public void AnUnknownStartDateIsLeftOutRatherThanGuessed()
    {
        RecordFileName.For("Sync", RecordFileName.Transcript, null, null, "txt")
            .Should().Be("Sync - Transcript.txt");

        // An unset column reads as 0001-01-01. Printing it would put a wrong answer that looks
        // like a right one into the name.
        RecordFileName.For("Sync", RecordFileName.Transcript, default(DateTime), null, "txt")
            .Should().Be("Sync - Transcript.txt");
    }

    [Fact]
    public void TheDateIsIsoSoAFolderOfWeeklyStandupsSortsByIt()
    {
        var january = RecordFileName.For("Daily standup", RecordFileName.Transcript,
            new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc), null, "txt");
        var september = RecordFileName.For("Daily standup", RecordFileName.Transcript, Started, null, "txt");

        january.Should().Be("Daily standup - Transcript - 2026-01-05.txt");
        string.CompareOrdinal(january, september).Should().BeNegative();
    }

    [Fact]
    public void TheSecondRecordingOfAMeetingSaysWhichOneItIs()
    {
        // Stop and restart inside one meeting and both passes share a title and a date. Without
        // the ordinal the browser renames the second one itself, in click order.
        RecordFileName.For("Sprint 42", RecordFileName.Recording, Started, null, "mp4", ordinal: 2)
            .Should().Be("Sprint 42 - Recording (2) - 2026-09-18.mp4");
        RecordFileName.For("Sprint 42", RecordFileName.Recording, Started, null, "mp4", ordinal: 3)
            .Should().Be("Sprint 42 - Recording (3) - 2026-09-18.mp4");
    }

    [Fact]
    public void TheOnlyRecordingIsJustTheRecording()
    {
        // A "(1)" on a lone file only makes the reader go looking for a second one.
        RecordFileName.For("Sprint 42", RecordFileName.Recording, Started, null, "mp4", ordinal: 1)
            .Should().Be("Sprint 42 - Recording - 2026-09-18.mp4");
    }

    [Fact]
    public void TheExtensionIsAcceptedWithOrWithoutItsDot()
    {
        RecordFileName.For("Sync", RecordFileName.Transcript, null, null, ".txt")
            .Should().Be("Sync - Transcript.txt");
    }
}
