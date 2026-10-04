using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Configuration;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.BackgroundProcessors;

namespace WarpTalk.TranslationRoomService.Tests.Workers;

/// <summary>
/// WT-930 — artifacts arrived after a meeting ended slower than they had to.
///
/// Finalization started by waiting 30 seconds for <c>translationRoom:{roomId}:final_processed</c>,
/// a channel nothing publishes. Every meeting paid the full 30 seconds before its transcript was
/// read and before the summary wait even began.
/// </summary>
public sealed class ArtifactsFinalizerSettleWindowTests
{
    [Fact]
    public async Task Finalization_WaitsOnlyTheShortSettleWindow_ThenMovesOn()
    {
        var roomId = Guid.NewGuid();
        var redis = new Mock<IRedisStateRepository>();
        TimeSpan? waited = null;
        string? channel = null;
        redis
            .Setup(r => r.WaitForSignalAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<string, TimeSpan, CancellationToken>((c, t, _) => (channel, waited) = (c, t))
            .ReturnsAsync(false);

        var events = new Mock<IAudioRouteEventProcessor>();
        // Refusing the transition ends the run right after the wait: that is all this test is about.
        events
            .Setup(e => e.ProcessEventAsync(roomId, null, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure("stop here"));

        var finalizer = CreateFinalizer(redis, events);

        await finalizer.ProcessRoomFinalizationAsync(roomId);

        Assert.Equal($"translationRoom:{roomId}:final_processed", channel);
        Assert.Equal(finalizer.TranscriptSettleWindow, waited);
        // A timed-out wait is the ordinary case, not a failure: finalization carries on.
        events.Verify(
            e => e.ProcessEventAsync(
                roomId, null, AudioRoutingEventType.flush_runtime.ToString(), "{}", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void SettleWindow_IsSeconds_NotHalfAMinute()
    {
        var finalizer = CreateFinalizer(new Mock<IRedisStateRepository>(), new Mock<IAudioRouteEventProcessor>());

        Assert.InRange(finalizer.TranscriptSettleWindow, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void ShorterSettleWindow_DoesNotShortenTheSummaryDeadline()
    {
        // The worker's summary is triggered when the meeting ends, so what matters is how long after
        // the END the finalizer stops waiting for it. That used to be 30s + 90s. Speeding up the
        // front must not hand a slow summary a tighter deadline.
        var finalizer = CreateFinalizer(new Mock<IRedisStateRepository>(), new Mock<IAudioRouteEventProcessor>());

        Assert.True(
            finalizer.TranscriptSettleWindow + finalizer.SummaryWaitTimeout >= TimeSpan.FromSeconds(120),
            "settle window + summary wait must still cover the old 120-second deadline");
    }

    private static ArtifactsFinalizer CreateFinalizer(
        Mock<IRedisStateRepository> redis,
        Mock<IAudioRouteEventProcessor> events) =>
        new(
            new Mock<IUnitOfWork>().Object,
            redis.Object,
            events.Object,
            NullLogger<ArtifactsFinalizer>.Instance,
            // Never reached: the run stops at the refused transition.
            null!,
            Options.Create(new ArtifactFinalizationSettings()),
            new Mock<ITranscriptCacheService>().Object,
            new Mock<IKnowledgeFactRequestPublisher>().Object);
}
