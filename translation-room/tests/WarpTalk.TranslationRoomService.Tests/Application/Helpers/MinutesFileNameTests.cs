using System.IO;
using System.Linq;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.Helpers;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

/// <summary>
/// What the downloaded file is called.
///
/// A name is not cosmetic here: it is how a person finds a signed record again six months later,
/// and how an operating system decides whether to accept the file at all.
/// </summary>
public class MinutesFileNameTests
{
    [Fact]
    public void TheNameCarriesBothTheNumberAndTheMeetingItRecords()
    {
        // The number alone leaves a folder of BB-2026-0001, -0002, -0003 that nobody can search.
        MinutesFileName.For("BB-2026-0001", "Họp sprint", 1, "docx")
            .Should().Be("BB-2026-0001 - Họp sprint.docx");
    }

    [Fact]
    public void TheNumberComesFirstSoAFolderOfMinutesStillSortsByRecord()
    {
        var name = MinutesFileName.For("BB-2026-0007", "Sprint review", 1, "pdf");

        name.Should().StartWith("BB-2026-0007");
        name.Should().EndWith(".pdf");
    }

    [Fact]
    public void OnlyARevisionSaysWhichRevisionItIs()
    {
        // An ordinary document must not arrive looking like a correction of an earlier one.
        MinutesFileName.For("BB-2026-0001", "Họp sprint", 1, "docx")
            .Should().NotContain("-v");
        MinutesFileName.For("BB-2026-0001", "Họp sprint", 3, "docx")
            .Should().Be("BB-2026-0001-v3 - Họp sprint.docx");
    }

    [Fact]
    public void AMeetingWithNoTitleStillGetsANameTheSystemCanWrite()
    {
        MinutesFileName.For("BB-2026-0001", null, 1, "docx").Should().Be("BB-2026-0001.docx");
        MinutesFileName.For("BB-2026-0001", "   ", 1, "docx").Should().Be("BB-2026-0001.docx");
    }

    [Fact]
    public void CharactersAnOperatingSystemRefusesAreRemoved()
    {
        // A title is user text. Somebody will put a slash in one, and a download that fails is
        // worse than a name with a space where the slash was.
        var name = MinutesFileName.For("BB-2026-0001", "Q3/Q4: kế hoạch <draft>?", 1, "docx");

        name.IndexOfAny(Path.GetInvalidFileNameChars()).Should().Be(-1);
        name.Should().Contain("kế hoạch");
    }

    [Fact]
    public void ATitleSpanningLinesBecomesOneLineRatherThanARunOfBlanks()
    {
        MinutesFileName.For("BB-2026-0001", "Họp sprint\n\nvà kế hoạch", 1, "docx")
            .Should().Be("BB-2026-0001 - Họp sprint và kế hoạch.docx");
    }

    [Fact]
    public void DiacriticsSurvive()
    {
        // Stripping them silently corrects the user's own words; HTTP carries UTF-8 names fine.
        MinutesFileName.For("BB-2026-0001", "Họp giao ban", 1, "docx")
            .Should().Contain("Họp giao ban");
    }

    [Fact]
    public void AVeryLongTitleIsCutAtAWordAndKeptShortEnoughToWrite()
    {
        var title = string.Join(" ", Enumerable.Repeat("kế hoạch", 40));

        var name = MinutesFileName.For("BB-2026-0001", title, 1, "docx");

        name.Length.Should().BeLessThan(120);
        name.Should().EndWith("….docx");
    }

    [Fact]
    public void AMinutesWithNoNumberStillProducesAWritableName()
    {
        MinutesFileName.For(null, "Họp sprint", 1, "docx")
            .Should().Be("bien-ban - Họp sprint.docx");
    }
}
