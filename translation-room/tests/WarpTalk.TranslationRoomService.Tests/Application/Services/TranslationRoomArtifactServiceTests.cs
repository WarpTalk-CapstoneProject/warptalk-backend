using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

public sealed class TranslationRoomArtifactServiceTests
{
    [Fact]
    public async Task GetArtifactDownloadAsync_ReturnsInlineTranscriptContent()
    {
        var userId = Guid.NewGuid();
        var artifact = CreateArtifact(userId);
        artifact.ArtifactType = "TRANSCRIPT_EXPORT";
        artifact.FileFormat = "markdown";
        artifact.Content = "# Real transcript";

        var service = CreateService(artifact);
        var result = await service.GetArtifactDownloadAsync(artifact.Id, userId);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.Url);
        Assert.Equal("# Real transcript", result.Value.Content);
        Assert.Equal("text/markdown", result.Value.ContentType);
        Assert.EndsWith(".md", result.Value.FileName);
    }

    [Fact]
    public async Task GetArtifactDownloadAsync_ReturnsProviderRecordingUrl()
    {
        var userId = Guid.NewGuid();
        var artifact = CreateArtifact(userId);
        artifact.ArtifactType = "OPTIONAL_RECORDING";
        artifact.FileFormat = "mp4";
        artifact.FileUrl = "https://storage.example/recording.mp4";
        artifact.ContainsRawAudio = true;

        var service = CreateService(artifact);
        var result = await service.GetArtifactDownloadAsync(artifact.Id, userId);

        Assert.True(result.IsSuccess);
        Assert.Equal(artifact.FileUrl, result.Value!.Url);
        Assert.Null(result.Value.Content);
    }

    [Fact]
    public async Task GetArtifactDownloadAsync_FailsWhenArtifactHasNoPayload()
    {
        var userId = Guid.NewGuid();
        var artifact = CreateArtifact(userId);

        var service = CreateService(artifact);
        var result = await service.GetArtifactDownloadAsync(artifact.Id, userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidState, result.ErrorCode);
    }

    private static TranslationRoomArtifactService CreateService(TranslationRoomArtifact artifact)
    {
        var repository = new Mock<ITranslationRoomArtifactRepository>();
        repository
            .Setup(repo => repo.GetArtifactWithRoomAsync(
                artifact.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(artifact);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(work => work.TranslationRoomArtifactRepository)
            .Returns(repository.Object);
        var signer = new Mock<IArtifactUrlSigner>();
        signer
            .Setup(item => item.CreateDownloadUrlAsync(
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, TimeSpan _, CancellationToken _) => url);
        return new TranslationRoomArtifactService(
            unitOfWork.Object,
            NullLogger<TranslationRoomArtifactService>.Instance,
            signer.Object,
            new Mock<IRedisStateRepository>().Object);
    }

    private static TranslationRoomArtifact CreateArtifact(Guid hostId)
    {
        var room = new TranslationRoom
        {
            Id = Guid.NewGuid(),
            HostId = hostId,
            Settings = "{}"
        };
        return new TranslationRoomArtifact
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = room.Id,
            TranslationRoom = room,
            ArtifactType = "TRANSCRIPT_EXPORT",
            Status = "COMPLETED"
        };
    }
}
