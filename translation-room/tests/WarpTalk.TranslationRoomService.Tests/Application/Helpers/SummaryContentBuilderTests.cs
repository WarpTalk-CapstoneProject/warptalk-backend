using System.Text.Json;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.Helpers;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

/// <summary>
/// The JSON the meeting page reads a summary from.
///
/// WT-379 extracted this from ArtifactsFinalizer so the late-summary recovery in
/// ArtifactsReconciliationWorker produces byte-identical content. A recovered summary and a
/// first-try summary are the same artifact seen at two different times, so any drift between two
/// copies of this logic would only ever show on the recovery path — the one nobody looks at.
/// These pin the shape both callers depend on.
/// </summary>
public class SummaryContentBuilderTests
{
    [Fact]
    public void Build_PassesStructuredJsonThroughVerbatim_WhenItIsAlreadyTheRightShape()
    {
        // The AI worker writes this shape directly. Re-serialising it would be a chance to lose
        // fields the frontend reads and this file does not know about.
        const string structured = """{"summary":"We shipped it.","decisions":["Ship Friday"],"actionItems":[]}""";

        var result = SummaryContentBuilder.Build(structured, null, null);

        result.Should().Be(structured);
    }

    [Fact]
    public void Build_FallsBackToTextReconstruction_WhenStructuredJsonIsMalformed()
    {
        var result = SummaryContentBuilder.Build("{not json", "A summary.", null);

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("summary").GetString().Should().Be("A summary.");
        doc.RootElement.GetProperty("insufficientData").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Build_MarksInsufficientData_WhenThereIsNothingAtAll()
    {
        // This is the artifact the finalizer writes when its 90s window closes, and the exact
        // state WT-379's recovery exists to replace. `insufficientData` is what the web client's
        // resolveSummaryState reads to say "No summary output".
        var result = SummaryContentBuilder.Build(null, null, null);

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("insufficientData").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Build_ParsesActionItemOwners()
    {
        var result = SummaryContentBuilder.Build(null, "A summary.", "[ ] Send the deck - @tuan");

        using var doc = JsonDocument.Parse(result);
        var item = doc.RootElement.GetProperty("actionItems")[0];
        item.GetProperty("owner").GetString().Should().Be("tuan");
        item.GetProperty("task").GetString().Should().Be("Send the deck");
    }

    [Fact]
    public void Build_KeepsAnActionItemThatNamesNobody()
    {
        var result = SummaryContentBuilder.Build(null, "A summary.", "[ ] Book the room");

        using var doc = JsonDocument.Parse(result);
        var item = doc.RootElement.GetProperty("actionItems")[0];
        item.GetProperty("owner").GetString().Should().BeEmpty();
        item.GetProperty("task").GetString().Should().Be("Book the room");
    }
}
