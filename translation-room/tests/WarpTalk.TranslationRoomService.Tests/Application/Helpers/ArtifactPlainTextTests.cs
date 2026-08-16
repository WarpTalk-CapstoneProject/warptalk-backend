using System.Text.Json;

using FluentAssertions;

using WarpTalk.TranslationRoomService.Application.Helpers;

using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

/// <summary>
/// The transcript and the summary are stored as markdown and as JSON, and both were handed to the
/// reader in that shape when they clicked Download: `**[Nam (VI)]**: xin chào`, and a wall of
/// `{"summary":…}`. The storage shapes are right for the code that reads them, so the rendering
/// happens on the way out — which is also what fixes the artifacts already in the database.
/// </summary>
public class ArtifactPlainTextTests
{
    private static string SummaryJson(object payload) => JsonSerializer.Serialize(payload);

    [Fact]
    public void Render_TurnsTheSummaryJsonIntoSomethingAPersonReads()
    {
        var content = SummaryJson(new
        {
            summary = "Agreed the release plan.",
            decisions = new[] { "Ship on Friday" },
            actionItems = new[] { new { owner = "Tu", task = "Cut the tag" } },
            insufficientData = false,
        });

        var text = ArtifactPlainText.Render("SUMMARY_EXPORT", content);

        text.Should().NotContain("{");
        text.Should().Contain("Agreed the release plan.");
        text.Should().Contain("Decisions:");
        text.Should().Contain("- Ship on Friday");
        text.Should().Contain("Action items:");
        text.Should().Contain("- Tu: Cut the tag");
    }

    [Fact]
    public void Render_SaysSoWhenThereWasNotEnoughMeetingToSummarise()
    {
        // MeetingSummaryKnowledgeText returns EMPTY for this case on purpose — indexing "could not
        // summarise" would make the workspace claim knowledge it does not have. A download has the
        // opposite duty: a 0-byte file is indistinguishable from a broken one.
        var text = ArtifactPlainText.Render(
            "SUMMARY_EXPORT",
            SummaryJson(new { summary = "", insufficientData = true }));

        text.Should().NotBeNullOrWhiteSpace();
        text.Should().Contain("not enough");
    }

    [Fact]
    public void Render_LeavesAnOlderProseSummaryAlone()
    {
        // Not every stored summary is the structured shape: older rows and one fallback path wrote
        // prose, which is already what this method is trying to produce.
        var text = ArtifactPlainText.Render("SUMMARY_EXPORT", "The team agreed to ship on Friday.");

        text.Should().Be("The team agreed to ship on Friday.");
    }

    [Fact]
    public void Render_StripsTheTranscriptsMarkdownWithoutLosingASpeaker()
    {
        var content =
            "# WarpTalk Transcription Room - Room: abc\nGenerated on: 2026-08-16 10:00:00 UTC\n---\n"
            + "**[Nam (VI)]**: xin chào\n**[Alex (EN)]**: hello";

        var text = ArtifactPlainText.Render("TRANSCRIPT_EXPORT", content)!;

        text.Should().NotContain("**");
        text.Should().NotContain("# ");
        text.Should().Contain("WarpTalk Transcription Room - Room: abc");
        text.Should().Contain("[Nam (VI)]: xin chào");
        text.Should().Contain("[Alex (EN)]: hello");
    }

    [Fact]
    public void Render_UnwrapsAWholeLineOfItalicsButNotAnAsteriskSomebodySaid()
    {
        var silent = ArtifactPlainText.Render(
            "TRANSCRIPT_EXPORT",
            "# Header\n---\n*No speech transcription recorded.*")!;
        silent.Should().Contain("No speech transcription recorded.");
        silent.Should().NotContain("*");

        // The emphasis markers are only removed when they wrap the ENTIRE line. An asterisk inside
        // speech is a character the speaker used.
        var spoken = ArtifactPlainText.Render(
            "TRANSCRIPT_EXPORT",
            "**[Nam (VI)]**: the file is named *.log")!;
        spoken.Should().Contain("the file is named *.log");
    }

    [Fact]
    public void Render_DoesNotTouchAnArtifactThatIsAFile()
    {
        // A recording is served as the file it is; there is no text to render and nothing here
        // may rewrite it.
        ArtifactPlainText.Render("RECORDING", "**not markdown, bytes**")
            .Should().Be("**not markdown, bytes**");
        ArtifactPlainText.IsTextExport("RECORDING").Should().BeFalse();
    }

    [Theory]
    [InlineData("TRANSCRIPT_EXPORT")]
    [InlineData("SUMMARY_EXPORT")]
    [InlineData("summary_export")]
    public void IsTextExport_CoversBothExportsCaseInsensitively(string type)
    {
        ArtifactPlainText.IsTextExport(type).Should().BeTrue();
    }
}
