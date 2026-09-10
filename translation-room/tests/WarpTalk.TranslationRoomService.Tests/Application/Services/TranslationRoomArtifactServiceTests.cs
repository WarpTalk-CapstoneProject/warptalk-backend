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
            new Mock<IRedisStateRepository>().Object,
            new Mock<IArtifactsFinalizationQueue>().Object);
    }

    /// <summary>
    /// THE REWRITE THAT HAD NOWHERE TO LAND.
    ///
    /// SummaryResultConsumerWorker replaces the room's SUMMARY_EXPORT and refuses to invent one.
    /// This method published the request anyway, so on a meeting that was never finalized — the
    /// one showing no summary, which is exactly where a person presses Regenerate — the button
    /// returned success, warptalk-ai spent an LLM call, and the consumer logged
    /// "No summary artifact to rewrite" and dropped the result. Every press.
    /// </summary>
    [Fact]
    public async Task RegenerateSummaryAsync_QueuesFinalization_WhenTheMeetingWasNeverFinalized()
    {
        var hostId = Guid.NewGuid();
        var room = CreateEndedRoom(hostId);
        var queue = new Mock<IArtifactsFinalizationQueue>();
        var redis = new Mock<IRedisStateRepository>();

        var service = CreateServiceForRoom(room, redis, queue);
        var result = await service.RegenerateSummaryAsync(room.Id, hostId, "general", null, "Bearer token");

        Assert.True(result.IsSuccess);
        queue.Verify(item => item.QueueFinalization(room.Id), Times.Once);
        // And no request published: there is nothing for the worker's answer to replace yet.
        redis.Verify(
            item => item.StreamAddAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()),
            Times.Never);
    }

    /// <summary>
    /// The normal path is untouched: a meeting with a summary artifact gets the request published
    /// and nothing re-finalized.
    /// </summary>
    [Fact]
    public async Task RegenerateSummaryAsync_PublishesTheRequest_WhenASummaryArtifactExists()
    {
        var hostId = Guid.NewGuid();
        var room = CreateEndedRoom(hostId);
        room.TranslationRoomArtifacts.Add(new TranslationRoomArtifact
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = room.Id,
            ArtifactType = "SUMMARY_EXPORT",
            Status = "COMPLETED"
        });

        var queue = new Mock<IArtifactsFinalizationQueue>();
        var redis = new Mock<IRedisStateRepository>();

        var service = CreateServiceForRoom(room, redis, queue);
        var result = await service.RegenerateSummaryAsync(room.Id, hostId, "general", null, "Bearer token");

        Assert.True(result.IsSuccess);
        queue.Verify(item => item.QueueFinalization(It.IsAny<Guid>()), Times.Never);
        redis.Verify(
            item => item.StreamAddAsync("assistant:summary_requests", It.IsAny<Dictionary<string, string>>()),
            Times.Once);
    }

    /// <summary>
    /// A rewrite carries the language the requester asked for, normalised to the bare ISO 639-1
    /// code the AI side keys its language names by.
    ///
    /// A room stores `vi-VN` and a picker sends `vi`. If the code that reached the worker
    /// depended on which spelling the caller happened to use, the same request would produce a
    /// summary in a language the prompt could not name — which reads to the model as no
    /// instruction at all, and quietly restores the guessing this replaced.
    /// </summary>
    [Fact]
    public async Task RegenerateSummaryAsync_ForwardsTheChosenLanguage_NormalisedToItsBareCode()
    {
        var hostId = Guid.NewGuid();
        var room = CreateEndedRoom(hostId);
        room.TranslationRoomArtifacts.Add(new TranslationRoomArtifact
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = room.Id,
            ArtifactType = "SUMMARY_EXPORT",
            Status = "COMPLETED"
        });

        Dictionary<string, string>? published = null;
        var redis = new Mock<IRedisStateRepository>();
        redis
            .Setup(item => item.StreamAddAsync("assistant:summary_requests", It.IsAny<Dictionary<string, string>>()))
            .Callback<string, Dictionary<string, string>>((_, fields) => published = fields);

        var service = CreateServiceForRoom(room, redis, new Mock<IArtifactsFinalizationQueue>());
        var result = await service.RegenerateSummaryAsync(room.Id, hostId, "general", "ja-JP", "Bearer token");

        Assert.True(result.IsSuccess);
        Assert.NotNull(published);
        Assert.Equal("ja", published!["summary_language"]);
    }

    /// <summary>
    /// Asking for no language is a real answer, not a missing one.
    ///
    /// It means "leave the language alone", which the AI side reads as "follow the transcript" —
    /// the behaviour every summary already in storage was written under. The field is still sent,
    /// empty, so the worker never has to tell an old message apart from a deliberate one.
    /// </summary>
    [Fact]
    public async Task RegenerateSummaryAsync_SendsAnEmptyLanguage_WhenTheCallerChoseNone()
    {
        var hostId = Guid.NewGuid();
        var room = CreateEndedRoom(hostId);
        room.TranslationRoomArtifacts.Add(new TranslationRoomArtifact
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = room.Id,
            ArtifactType = "SUMMARY_EXPORT",
            Status = "COMPLETED"
        });

        Dictionary<string, string>? published = null;
        var redis = new Mock<IRedisStateRepository>();
        redis
            .Setup(item => item.StreamAddAsync("assistant:summary_requests", It.IsAny<Dictionary<string, string>>()))
            .Callback<string, Dictionary<string, string>>((_, fields) => published = fields);

        var service = CreateServiceForRoom(room, redis, new Mock<IArtifactsFinalizationQueue>());
        var result = await service.RegenerateSummaryAsync(room.Id, hostId, "general", null, "Bearer token");

        Assert.True(result.IsSuccess);
        Assert.NotNull(published);
        Assert.Equal(string.Empty, published!["summary_language"]);
    }

    /// <summary>
    /// A transcript with no summary is not a gap finalization can fill: FinalizeRoomArtifactsAsync
    /// writes both in one save, so running it again would give the meeting a SECOND transcript
    /// rather than its first summary. Said out loud instead of guessed at.
    /// </summary>
    [Fact]
    public async Task RegenerateSummaryAsync_RefusesToRefinalize_WhenOnlyTheSummaryIsMissing()
    {
        var hostId = Guid.NewGuid();
        var room = CreateEndedRoom(hostId);
        room.TranslationRoomArtifacts.Add(new TranslationRoomArtifact
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = room.Id,
            ArtifactType = "TRANSCRIPT_EXPORT",
            Status = "COMPLETED"
        });

        var queue = new Mock<IArtifactsFinalizationQueue>();
        var redis = new Mock<IRedisStateRepository>();

        var service = CreateServiceForRoom(room, redis, queue);
        var result = await service.RegenerateSummaryAsync(room.Id, hostId, "general", null, "Bearer token");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidState, result.ErrorCode);
        queue.Verify(item => item.QueueFinalization(It.IsAny<Guid>()), Times.Never);
        redis.Verify(
            item => item.StreamAddAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()),
            Times.Never);
    }

    private static TranslationRoom CreateEndedRoom(Guid hostId) => new()
    {
        Id = Guid.NewGuid(),
        HostId = hostId,
        WorkspaceId = Guid.NewGuid(),
        Status = "ENDED",
        TargetLanguages = "[]",
        Settings = "{}"
    };

    private static TranslationRoomArtifactService CreateServiceForRoom(
        TranslationRoom room,
        Mock<IRedisStateRepository> redis,
        Mock<IArtifactsFinalizationQueue> queue)
    {
        var roomRepository = new Mock<ITranslationRoomRepository>();
        roomRepository
            .Setup(repo => repo.FirstOrDefaultAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<TranslationRoom, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(work => work.TranslationRoomRepository).Returns(roomRepository.Object);

        return new TranslationRoomArtifactService(
            unitOfWork.Object,
            NullLogger<TranslationRoomArtifactService>.Instance,
            new Mock<IArtifactUrlSigner>().Object,
            redis.Object,
            queue.Object);
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
