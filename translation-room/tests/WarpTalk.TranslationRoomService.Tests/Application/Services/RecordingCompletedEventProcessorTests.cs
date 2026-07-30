using System.Linq.Expressions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.Shared.Events;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

public sealed class RecordingCompletedEventProcessorTests
{
    [Fact]
    public async Task ProcessAsync_CreatesRecordingArtifact_WithProviderIdempotencyKey()
    {
        var artifactRepository = new Mock<ITranslationRoomArtifactRepository>();
        artifactRepository.Setup(repository => repository.AnyAsync(
                It.IsAny<Expression<Func<TranslationRoomArtifact, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        TranslationRoomArtifact? persistedArtifact = null;
        artifactRepository.Setup(repository => repository.AddAsync(
                It.IsAny<TranslationRoomArtifact>(),
                It.IsAny<CancellationToken>()))
            .Callback<TranslationRoomArtifact, CancellationToken>(
                (artifact, _) => persistedArtifact = artifact)
            .Returns(Task.CompletedTask);
        var unitOfWork = CreateUnitOfWork(artifactRepository);
        var sut = new RecordingCompletedEventProcessor(
            unitOfWork.Object,
            NullLogger<RecordingCompletedEventProcessor>.Instance);
        var envelope = CreateEnvelope();

        var result = await sut.ProcessAsync(envelope, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
        Assert.NotNull(persistedArtifact);
        Assert.Equal(envelope.Payload.TranslationRoomId, persistedArtifact.TranslationRoomId);
        Assert.Equal(envelope.Payload.EgressId, persistedArtifact.ProviderArtifactId);
        Assert.Equal("OPTIONAL_RECORDING", persistedArtifact.ArtifactType);
        Assert.Equal("COMPLETED", persistedArtifact.Status);
        Assert.True(persistedArtifact.ContainsRawAudio);
        Assert.True(persistedArtifact.ContainsRawVideo);
        unitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_IsIdempotent_WhenProviderArtifactAlreadyExists()
    {
        var artifactRepository = new Mock<ITranslationRoomArtifactRepository>();
        artifactRepository.Setup(repository => repository.AnyAsync(
                It.IsAny<Expression<Func<TranslationRoomArtifact, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var unitOfWork = CreateUnitOfWork(artifactRepository);
        var sut = new RecordingCompletedEventProcessor(
            unitOfWork.Object,
            NullLogger<RecordingCompletedEventProcessor>.Instance);

        var result = await sut.ProcessAsync(CreateEnvelope(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value);
        artifactRepository.Verify(
            repository => repository.AddAsync(
                It.IsAny<TranslationRoomArtifact>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        unitOfWork.Verify(
            work => work.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static Mock<IUnitOfWork> CreateUnitOfWork(
        Mock<ITranslationRoomArtifactRepository> artifactRepository)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(work => work.TranslationRoomArtifactRepository)
            .Returns(artifactRepository.Object);
        unitOfWork.Setup(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        return unitOfWork;
    }

    private static EventEnvelope<MeetingRecordingCompletedEventPayload> CreateEnvelope()
        => DomainEventEnvelope.Create(
            MeetingEventTypes.RecordingCompleted,
            "meeting-service",
            workspaceId: null,
            new MeetingRecordingCompletedEventPayload(
                Guid.NewGuid(),
                "EG_123",
                "s3://recordings/room.mp4",
                "mp4",
                4096,
                ContainsRawAudio: true,
                ContainsRawVideo: true));
}
