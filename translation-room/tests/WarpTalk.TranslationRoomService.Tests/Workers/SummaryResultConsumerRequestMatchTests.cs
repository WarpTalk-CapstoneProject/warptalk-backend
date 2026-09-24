using WarpTalk.TranslationRoomService.API.Workers;

namespace WarpTalk.TranslationRoomService.Tests.Workers;

/// <summary>
/// The open half of WT-701: a rendering filed under a different pair than the one it was asked
/// for is never found by the endpoint, and a retry reproduces the same mismatch. The consumer has
/// both pairs in hand, so it reports that as a failure with a reason instead of a silent success.
/// </summary>
public sealed class SummaryResultConsumerRequestMatchTests
{
    private const string Vietnamese = "{\"summary\":\"x\",\"templateKey\":\"general\",\"summaryLanguage\":\"vi\"}";

    [Fact]
    public void ARenderingInTheRequestedPairMatches() =>
        Assert.Null(SummaryResultConsumerWorker.DescribeRequestMismatch(
            new Dictionary<string, string> { ["requested_template_key"] = "general", ["summary_language"] = "vi" },
            Vietnamese));

    [Theory]
    [InlineData("vi-VN")]
    [InlineData("vi_VN")]
    [InlineData("VI")]
    public void AnyspellingOfTheRequestedLanguageMatches(string requested) =>
        Assert.Null(SummaryResultConsumerWorker.DescribeRequestMismatch(
            new Dictionary<string, string> { ["requested_template_key"] = "General", ["summary_language"] = requested },
            Vietnamese));

    [Fact]
    public void ARenderingStampedInAnotherLanguageIsAMismatchThatSaysWhich()
    {
        var mismatch = SummaryResultConsumerWorker.DescribeRequestMismatch(
            new Dictionary<string, string> { ["requested_template_key"] = "general", ["summary_language"] = "vi" },
            "{\"summary\":\"x\",\"templateKey\":\"general\",\"summaryLanguage\":\"\"}");

        Assert.NotNull(mismatch);
        Assert.Contains("as spoken", mismatch);
        Assert.Contains("vi", mismatch);
    }

    [Fact]
    public void ARenderingInAnotherShapeIsAMismatch() =>
        Assert.NotNull(SummaryResultConsumerWorker.DescribeRequestMismatch(
            new Dictionary<string, string> { ["requested_template_key"] = "retro", ["summary_language"] = "vi" },
            Vietnamese));

    /// <summary>A worker that predates the echo fields keeps the old behaviour: nothing to compare.</summary>
    [Fact]
    public void AResultWithoutTheEchoIsNotJudged() =>
        Assert.Null(SummaryResultConsumerWorker.DescribeRequestMismatch(
            new Dictionary<string, string> { ["template_key"] = "general" },
            "{\"summary\":\"x\",\"templateKey\":\"standup\",\"summaryLanguage\":\"ja\"}"));
}
