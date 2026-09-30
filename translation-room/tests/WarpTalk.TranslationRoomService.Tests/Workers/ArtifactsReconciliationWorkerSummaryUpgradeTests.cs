using WarpTalk.TranslationRoomService.API.Workers;
using WarpTalk.TranslationRoomService.Application.Helpers;

namespace WarpTalk.TranslationRoomService.Tests.Workers;

/// <summary>
/// WT-701. The finalizer now keeps <c>meeting:{id}:summary</c> when it saved only the markdown
/// fallback, so the late-summary recovery has to tell a replaceable summary from a structured one
/// instead of trusting the key's existence alone.
/// </summary>
public sealed class ArtifactsReconciliationWorkerSummaryUpgradeTests
{
    [Fact]
    public void TheMarkdownFallbackIsUpgradable() =>
        Assert.True(ArtifactsReconciliationWorker.IsUpgradableSummary(
            SummaryContentBuilder.Build(null, "## Meeting Summary", "[ ] Ship it - @an")));

    [Fact]
    public void TheInsufficientDataPlaceholderIsUpgradable() =>
        Assert.True(ArtifactsReconciliationWorker.IsUpgradableSummary(
            SummaryContentBuilder.Build(null, null, null)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public void UnreadableContentIsUpgradable(string? content) =>
        Assert.True(ArtifactsReconciliationWorker.IsUpgradableSummary(content));

    [Fact]
    public void AStructuredSummaryIsNeverOverwritten() =>
        Assert.False(ArtifactsReconciliationWorker.IsUpgradableSummary(
            "{\"summary\":\"x\",\"decisions\":[],\"actionItems\":[],\"templateKey\":\"standup\",\"summaryLanguage\":\"ja\"}"));
}
