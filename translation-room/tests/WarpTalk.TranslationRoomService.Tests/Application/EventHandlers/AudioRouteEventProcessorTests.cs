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
