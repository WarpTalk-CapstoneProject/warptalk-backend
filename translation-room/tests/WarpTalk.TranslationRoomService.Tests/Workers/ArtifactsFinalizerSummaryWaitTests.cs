using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using WarpTalk.TranslationRoomService.API.Workers;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Configuration;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.BackgroundProcessors;

namespace WarpTalk.TranslationRoomService.Tests.Workers;

/// <summary>
/// WT-701 — the Recap rail showed "## Meeting Summary - **Purpose:** ..." as one paragraph right
/// after a meeting ended, and the real structured summary only after a reload minutes later.
///
/// ai_assistant_worker writes the summary hash one field at a time with an LLM call between
/// each: content, then action_items, then structured_json. The finalizer stopped waiting on the
/// FIRST field, saved the markdown fallback, and deleted the key the structured version was about
/// to land in.
/// </summary>
public sealed class ArtifactsFinalizerSummaryWaitTests
{
    private const string Markdown = "## Meeting Summary\n- **Purpose:** plan the sprint";
    private const string Structured =
        "{\"summary\":\"Planned the sprint.\",\"decisions\":[],\"actionItems\":[],\"templateKey\":\"general\",\"summaryLanguage\":\"en\"}";

    [Fact]
    public async Task ContentAloneDoesNotEndTheWait_TheStructuredSummaryIsWhatGetsSaved()
    {
        var roomId = Guid.NewGuid();
        var summaryKey = $"meeting:{roomId}:summary";
        var redis = new Mock<IRedisStateRepository>();
        var structuredReads = 0;

        redis.Setup(r => r.HashGetAsync(summaryKey, MeetingSummaryHash.Content)).ReturnsAsync(Markdown);
        redis.Setup(r => r.HashGetAsync(summaryKey, MeetingSummaryHash.ActionItems)).ReturnsAsync((string?)null);
        // Absent on the first two polls, present from the third: the worker is still between hsets.
        redis.Setup(r => r.HashGetAsync(summaryKey, MeetingSummaryHash.StructuredJson))
            .ReturnsAsync(() => ++structuredReads >= 3 ? Structured : null);

        var finalizer = CreateFinalizer(redis, TimeSpan.FromSeconds(10));

        var result = await finalizer.FinalizeSummaryAsync(roomId, CancellationToken.None);

        Assert.Equal(Structured, result.Artifact.Content);
        Assert.False(result.TimedOut);
        Assert.True(structuredReads >= 3);
        redis.Verify(r => r.KeyDeleteAsync(summaryKey), Times.Once);
    }

    [Fact]
    public async Task ContentOnlyAtTheDeadline_SavesTheFallbackAndKeepsTheKeyForTheUpgrade()
    {
        var roomId = Guid.NewGuid();
        var summaryKey = $"meeting:{roomId}:summary";
        var redis = new Mock<IRedisStateRepository>();

        redis.Setup(r => r.HashGetAsync(summaryKey, MeetingSummaryHash.Content)).ReturnsAsync(Markdown);
        redis.Setup(r => r.HashGetAsync(summaryKey, MeetingSummaryHash.ActionItems)).ReturnsAsync("[ ] Ship it - @an");
        redis.Setup(r => r.HashGetAsync(summaryKey, MeetingSummaryHash.StructuredJson)).ReturnsAsync((string?)null);

        var finalizer = CreateFinalizer(redis, TimeSpan.FromMilliseconds(50));

        var result = await finalizer.FinalizeSummaryAsync(roomId, CancellationToken.None);

        Assert.Equal(SummaryContentBuilder.Build(null, Markdown, "[ ] Ship it - @an"), result.Artifact.Content);
        // Not a timeout for the retry's purposes: there IS a summary, only not the structured one.
        Assert.False(result.TimedOut);
        redis.Verify(r => r.KeyDeleteAsync(It.IsAny<string>()), Times.Never);
        // And the fallback it saved is exactly what the reconciliation worker is allowed to upgrade.
        Assert.True(ArtifactsReconciliationWorker.IsUpgradableSummary(result.Artifact.Content));
    }

    [Fact]
    public async Task StructuredJsonAlreadyThere_CostsOneReadAndDeletesTheKey()
    {
        var roomId = Guid.NewGuid();
        var summaryKey = $"meeting:{roomId}:summary";
        var redis = new Mock<IRedisStateRepository>();

        redis.Setup(r => r.HashGetAsync(summaryKey, MeetingSummaryHash.Content)).ReturnsAsync(Markdown);
        redis.Setup(r => r.HashGetAsync(summaryKey, MeetingSummaryHash.ActionItems)).ReturnsAsync((string?)null);
        redis.Setup(r => r.HashGetAsync(summaryKey, MeetingSummaryHash.StructuredJson)).ReturnsAsync(Structured);

        var finalizer = CreateFinalizer(redis, TimeSpan.FromSeconds(10));

        var result = await finalizer.FinalizeSummaryAsync(roomId, CancellationToken.None);

        Assert.Equal(Structured, result.Artifact.Content);
        redis.Verify(r => r.HashGetAsync(summaryKey, MeetingSummaryHash.StructuredJson), Times.Once);
        redis.Verify(r => r.KeyDeleteAsync(summaryKey), Times.Once);
    }

    private static ArtifactsFinalizer CreateFinalizer(Mock<IRedisStateRepository> redis, TimeSpan wait) =>
        new(
            new Mock<IUnitOfWork>().Object,
            redis.Object,
            new Mock<IAudioRouteEventProcessor>().Object,
            NullLogger<ArtifactsFinalizer>.Instance,
            // The summary half never touches the transcript client.
            null!,
            Options.Create(new ArtifactFinalizationSettings()),
            new Mock<ITranscriptCacheService>().Object,
            new Mock<IKnowledgeFactRequestPublisher>().Object)
        {
            SummaryWaitTimeout = wait,
            SummaryPollInterval = TimeSpan.FromMilliseconds(10)
        };
}
