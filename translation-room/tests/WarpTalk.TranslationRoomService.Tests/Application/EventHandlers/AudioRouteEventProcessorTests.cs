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
}
