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
    /// <summary>
    /// The transcript is STORED as markdown and DOWNLOADED as plain text.
    ///
    /// This test used to assert the stored bytes back — `# Real transcript`, text/markdown, .md —
    /// which was the old contract and the reason a person who clicked Download got
    /// `**[Nam (VI)]**: xin chào`. The storage shape is deliberately unchanged (the web client
    /// parses the summary's JSON, the knowledge indexer reads the same field); what changed is
    /// that the download renders it. See ArtifactPlainText.
    /// </summary>
    [Fact]
    public async Task GetArtifactDownloadAsync_ServesTheTranscriptAsPlainText()
    {
        var userId = Guid.NewGuid();
        var artifact = CreateArtifact(userId);
        artifact.ArtifactType = "TRANSCRIPT_EXPORT";
        artifact.FileFormat = "markdown";
        artifact.Content = "# Real transcript\n---\n**[Nam (VI)]**: xin chào";

        var service = CreateService(artifact);
        var result = await service.GetArtifactDownloadAsync(artifact.Id, userId);

        Assert.True(result.IsSuccess);
        // A text export has no file behind it — the content IS the artifact — so no signed URL.
        Assert.Null(result.Value!.Url);
        Assert.Equal("text/plain", result.Value.ContentType);
        Assert.EndsWith(".txt", result.Value.FileName);
        Assert.DoesNotContain("**", result.Value.Content);
        Assert.DoesNotContain("# ", result.Value.Content);
        Assert.Contains("Real transcript", result.Value.Content);
        Assert.Contains("[Nam (VI)]: xin chào", result.Value.Content);
    }

    /// <summary>
    /// The format switch still decides for everything that is a real file. Only the two text
    /// exports are rendered on the way out; a markdown artifact of any other type is served as
    /// the markdown it is.
    /// </summary>
    [Fact]
    public async Task GetArtifactDownloadAsync_LeavesANonTextExportOnItsStoredFormat()
    {
        var userId = Guid.NewGuid();
        var artifact = CreateArtifact(userId);
        artifact.ArtifactType = "DEBUG_LOG";
        artifact.FileFormat = "markdown";
        artifact.Content = "# Raw notes";

        var service = CreateService(artifact);
        var result = await service.GetArtifactDownloadAsync(artifact.Id, userId);

        Assert.True(result.IsSuccess);
        Assert.Equal("# Raw notes", result.Value!.Content);
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
