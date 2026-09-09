using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.EventHandlers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.EventHandlers;

public class AudioRouteEventProcessorTests
{
    [Fact]
    public async Task SessionStarts_PublishesRoomLifecycle_WhenRoomHasNoAudioRoutes()
    {
        var roomId = Guid.NewGuid();
        var unitOfWork = new Mock<IUnitOfWork>();
        var routeRepository = new Mock<ITranslationRoomAudioRouteRepository>();
        var roomRepository = new Mock<ITranslationRoomRepository>();
        var cacheService = new Mock<IAudioRouteCacheService>();

        routeRepository
            .Setup(repository => repository.GetRoutesByRoomIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomAudioRoute>());
        unitOfWork.SetupGet(work => work.TranslationRoomAudioRouteRepository).Returns(routeRepository.Object);
        unitOfWork.SetupGet(work => work.TranslationRoomRepository).Returns(roomRepository.Object);
        cacheService
            .Setup(service => service.PublishRoutesUpdateAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var processor = new AudioRouteEventProcessor(
            Mock.Of<IAudioRouteTransitionProcessor>(),
            unitOfWork.Object,
            Mock.Of<IRedisStateRepository>(),
            Mock.Of<IArtifactsFinalizationQueue>(),
            Mock.Of<ITelemetryStateService>(),
            cacheService.Object,
            Mock.Of<ILogger<AudioRouteEventProcessor>>());

        var result = await processor.ProcessEventAsync(
            roomId,
            null,
            AudioRoutingEventType.session_starts.ToString(),
            "{}");

        Assert.True(result.IsSuccess);
        cacheService.Verify(
            service => service.PublishRoutesUpdateAsync(roomId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SessionEnds_PublishesRoomLifecycle_WhenRoomHasNoAudioRoutes()
    {
        // WT-314. Audio routes only exist once someone pressed Start Translation, so a meeting
        // nobody ever started ends with zero routes — and the publish used to be skipped
        // entirely. That AUDIO_ROUTES_UPDATED is the only signal that releases
        // livekit_ingress_worker's "AIBot_{room}" participant, which MeetingRoomService
        // summons on every JoinMeetingAsync. Without it the bot stayed connected indefinitely,
        // billing LiveKit connection minutes; and because the bot is itself a participant,
        // LiveKit's own empty_timeout never collected the room either.
        var roomId = Guid.NewGuid();
        var unitOfWork = new Mock<IUnitOfWork>();
        var routeRepository = new Mock<ITranslationRoomAudioRouteRepository>();
        var roomRepository = new Mock<ITranslationRoomRepository>();
        var cacheService = new Mock<IAudioRouteCacheService>();

        routeRepository
            .Setup(repository => repository.GetRoutesByRoomIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomAudioRoute>());
        unitOfWork.SetupGet(work => work.TranslationRoomAudioRouteRepository).Returns(routeRepository.Object);
        unitOfWork.SetupGet(work => work.TranslationRoomRepository).Returns(roomRepository.Object);
        cacheService
            .Setup(service => service.PublishRoutesUpdateAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var processor = new AudioRouteEventProcessor(
            Mock.Of<IAudioRouteTransitionProcessor>(),
            unitOfWork.Object,
            Mock.Of<IRedisStateRepository>(),
            Mock.Of<IArtifactsFinalizationQueue>(),
            Mock.Of<ITelemetryStateService>(),
            cacheService.Object,
            Mock.Of<ILogger<AudioRouteEventProcessor>>());

        var result = await processor.ProcessEventAsync(
            roomId,
            null,
            AudioRoutingEventType.session_ends.ToString(),
            "{}");

        Assert.True(result.IsSuccess);
        cacheService.Verify(
            service => service.PublishRoutesUpdateAsync(roomId, It.IsAny<CancellationToken>()),
            Times.Once);
        // No route changed, so nothing should have been written.
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SessionEnds_QueuesFinalization_WhenRoomHasNoAudioRoutes()
    {
        // THE ROOT CAUSE OF "0 artifacts beside a full transcript".
        //
        // Finalization used to be queued only from inside the `routesToUpdate.Any()` block, and
        // routes only exist once somebody pressed Start Translation. A transcript-only meeting
        // therefore ended with nothing queued: no TRANSCRIPT_EXPORT, no SUMMARY_EXPORT, and so
        // nothing for the late-summary recovery or the saved-transcript fallback to fill in.
        // ArtifactsReconciliationWorker did eventually catch it, but only after a 10-minute grace
        // plus a 5-minute sweep — past the 15-minute window the room page polls in.
        var roomId = Guid.NewGuid();
        var unitOfWork = new Mock<IUnitOfWork>();
        var routeRepository = new Mock<ITranslationRoomAudioRouteRepository>();
        var roomRepository = new Mock<ITranslationRoomRepository>();
        var cacheService = new Mock<IAudioRouteCacheService>();
        var finalizationQueue = new Mock<IArtifactsFinalizationQueue>();

        routeRepository
            .Setup(repository => repository.GetRoutesByRoomIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomAudioRoute>());
        roomRepository
            .Setup(repository => repository.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationRoom { Id = roomId, Status = RoomStatus.ENDED.ToString() });
        unitOfWork.SetupGet(work => work.TranslationRoomAudioRouteRepository).Returns(routeRepository.Object);
        unitOfWork.SetupGet(work => work.TranslationRoomRepository).Returns(roomRepository.Object);
        cacheService
            .Setup(service => service.PublishRoutesUpdateAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var processor = new AudioRouteEventProcessor(
            Mock.Of<IAudioRouteTransitionProcessor>(),
            unitOfWork.Object,
            Mock.Of<IRedisStateRepository>(),
            finalizationQueue.Object,
            Mock.Of<ITelemetryStateService>(),
            cacheService.Object,
            Mock.Of<ILogger<AudioRouteEventProcessor>>());

        var result = await processor.ProcessEventAsync(
            roomId,
            null,
            AudioRoutingEventType.session_ends.ToString(),
            "{}");

        Assert.True(result.IsSuccess);
        finalizationQueue.Verify(queue => queue.QueueFinalization(roomId), Times.Once);
    }

    [Theory]
    [InlineData("CANCELLED")]
    [InlineData("EXPIRED")]
    public async Task SessionEnds_DoesNotQueueFinalization_ForAMeetingThatNeverHappened(string status)
    {
        // Cancel and Expire publish the SAME room-scoped session_ends (PublishTerminalLifecycleAsync)
        // and are reachable only from SCHEDULED/WAITING. Finalizing one would write two empty
        // artifacts and ring MEETING_SUMMARY_READY for a meeting nobody attended, so the room's
        // own status — not the mere arrival of the event — is what authorises finalization.
        var roomId = Guid.NewGuid();
        var unitOfWork = new Mock<IUnitOfWork>();
        var routeRepository = new Mock<ITranslationRoomAudioRouteRepository>();
        var roomRepository = new Mock<ITranslationRoomRepository>();
        var cacheService = new Mock<IAudioRouteCacheService>();
        var finalizationQueue = new Mock<IArtifactsFinalizationQueue>();

        routeRepository
            .Setup(repository => repository.GetRoutesByRoomIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomAudioRoute>());
        roomRepository
            .Setup(repository => repository.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationRoom { Id = roomId, Status = status });
        unitOfWork.SetupGet(work => work.TranslationRoomAudioRouteRepository).Returns(routeRepository.Object);
        unitOfWork.SetupGet(work => work.TranslationRoomRepository).Returns(roomRepository.Object);
        cacheService
            .Setup(service => service.PublishRoutesUpdateAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var processor = new AudioRouteEventProcessor(
            Mock.Of<IAudioRouteTransitionProcessor>(),
            unitOfWork.Object,
            Mock.Of<IRedisStateRepository>(),
            finalizationQueue.Object,
            Mock.Of<ITelemetryStateService>(),
            cacheService.Object,
            Mock.Of<ILogger<AudioRouteEventProcessor>>());

        var result = await processor.ProcessEventAsync(
            roomId,
            null,
            AudioRoutingEventType.session_ends.ToString(),
            "{}");

        Assert.True(result.IsSuccess);
        finalizationQueue.Verify(queue => queue.QueueFinalization(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task SessionEnds_DoesNotQueueFinalizationTwice_WhenARouteAlsoEnded()
    {
        // The route-driven enqueue is still the normal path. Reading the room back must not turn
        // one ended meeting into two finalizations — which would give it two of every artifact,
        // and the page picks whichever it sees first.
        var roomId = Guid.NewGuid();
        var route = new TranslationRoomAudioRoute
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = roomId,
            Status = AudioRouteStatus.ENDING.ToString()
        };

        var unitOfWork = new Mock<IUnitOfWork>();
        var routeRepository = new Mock<ITranslationRoomAudioRouteRepository>();
        var roomRepository = new Mock<ITranslationRoomRepository>();
        var cacheService = new Mock<IAudioRouteCacheService>();
        var finalizationQueue = new Mock<IArtifactsFinalizationQueue>();
        var transitionProcessor = new Mock<IAudioRouteTransitionProcessor>();

        transitionProcessor
            .Setup(processor => processor.ProcessTransition(
                It.IsAny<TranslationRoomAudioRoute>(),
                It.IsAny<AudioRoutingEventType>(),
                It.IsAny<string>()))
            .Returns(true);
        routeRepository
            .Setup(repository => repository.GetRoutesByRoomIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomAudioRoute> { route });
        roomRepository
            .Setup(repository => repository.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationRoom { Id = roomId, Status = RoomStatus.ENDED.ToString() });
        unitOfWork.SetupGet(work => work.TranslationRoomAudioRouteRepository).Returns(routeRepository.Object);
        unitOfWork.SetupGet(work => work.TranslationRoomRepository).Returns(roomRepository.Object);
        cacheService
            .Setup(service => service.PublishRoutesUpdateAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var processor = new AudioRouteEventProcessor(
            transitionProcessor.Object,
            unitOfWork.Object,
            Mock.Of<IRedisStateRepository>(),
            finalizationQueue.Object,
            Mock.Of<ITelemetryStateService>(),
            cacheService.Object,
            Mock.Of<ILogger<AudioRouteEventProcessor>>());

        var result = await processor.ProcessEventAsync(
            roomId,
            null,
            AudioRoutingEventType.session_ends.ToString(),
            "{}");

        Assert.True(result.IsSuccess);
        finalizationQueue.Verify(queue => queue.QueueFinalization(roomId), Times.Once);
    }

    [Fact]
    public async Task NonLifecycleEvent_DoesNotPublish_WhenNoRouteChanged()
    {
        // The lifecycle allow-list must stay an allow-list: a telemetry-shaped event that
        // moved no route has nothing to tell the AI pipeline.
        var roomId = Guid.NewGuid();
        var unitOfWork = new Mock<IUnitOfWork>();
        var routeRepository = new Mock<ITranslationRoomAudioRouteRepository>();
        var roomRepository = new Mock<ITranslationRoomRepository>();
        var cacheService = new Mock<IAudioRouteCacheService>();

        routeRepository
            .Setup(repository => repository.GetRoutesByRoomIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomAudioRoute>());
        unitOfWork.SetupGet(work => work.TranslationRoomAudioRouteRepository).Returns(routeRepository.Object);
        unitOfWork.SetupGet(work => work.TranslationRoomRepository).Returns(roomRepository.Object);

        var processor = new AudioRouteEventProcessor(
            Mock.Of<IAudioRouteTransitionProcessor>(),
            unitOfWork.Object,
            Mock.Of<IRedisStateRepository>(),
            Mock.Of<IArtifactsFinalizationQueue>(),
            Mock.Of<ITelemetryStateService>(),
            cacheService.Object,
            Mock.Of<ILogger<AudioRouteEventProcessor>>());

        var result = await processor.ProcessEventAsync(
            roomId,
            null,
            AudioRoutingEventType.outputs_linked.ToString(),
            "{}");

        Assert.True(result.IsSuccess);
        cacheService.Verify(
            service => service.PublishRoutesUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
