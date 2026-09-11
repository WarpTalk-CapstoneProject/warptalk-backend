using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Constants;
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
        queue.Verify(item => item.QueueFinalization(room.Id, "general", ""), Times.Once);
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
        queue.Verify(
            item => item.QueueFinalization(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);
        redis.Verify(
            item => item.StreamAddAsync("assistant:summary_requests", It.IsAny<Dictionary<string, string>>()),
            Times.Once);
    }

    /// <summary>
    /// THE SHAPE A PERSON ASKED FOR SURVIVES THE REDIRECT.
    ///
    /// This is the bug reported as "summary type không hoạt động", and it lived in the gap
    /// between two correct decisions. Redirecting to finalization is right — the meeting needs
    /// drawing up, not re-summarising. Finalizing in the default shape is right too, because a
    /// finalizer cannot know a meeting was really a standup.
    ///
    /// But on THIS path the host had just said so, and their answer stopped at the redirect. The
    /// request returned success, a General summary arrived, the picker snapped back to General,
    /// and from the host's side pressing "Standup" did nothing at all — on the meeting this
    /// method's own comments call the one they are most likely to press it on.
    /// </summary>
    [Fact]
    public async Task RegenerateSummaryAsync_CarriesTheChosenShapeIntoTheFinalizationItRedirectsTo()
    {
        var hostId = Guid.NewGuid();
        var room = CreateEndedRoom(hostId);
        var queue = new Mock<IArtifactsFinalizationQueue>();

        var service = CreateServiceForRoom(room, new Mock<IRedisStateRepository>(), queue);
        var result = await service.RegenerateSummaryAsync(room.Id, hostId, "standup", "ja", "Bearer token");

        Assert.True(result.IsSuccess);
        queue.Verify(item => item.QueueFinalization(room.Id, "standup", "ja"), Times.Once);
    }

    /// <summary>
    /// A locale tag is normalised on the way in, exactly as it is on the request-publishing path,
    /// so the same choice cannot mean two things depending on which branch it took.
    /// </summary>
    [Fact]
    public async Task TheRedirectNormalisesTheLanguageTheSameWayThePublishedRequestDoes()
    {
        var hostId = Guid.NewGuid();
        var room = CreateEndedRoom(hostId);
        var queue = new Mock<IArtifactsFinalizationQueue>();

        var service = CreateServiceForRoom(room, new Mock<IRedisStateRepository>(), queue);
        await service.RegenerateSummaryAsync(room.Id, hostId, "  STANDUP  ", "vi-VN", "Bearer token");

        queue.Verify(item => item.QueueFinalization(room.Id, "standup", "vi"), Times.Once);
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

    /// <summary>
    /// WT-669: the id is the only handle the requester has on an answer that arrives later.
    ///
    /// It used to be generated straight into the stream dictionary and forgotten on the same
    /// line, which is why every way a rewrite could fail reached a log and stopped there.
    /// </summary>
    [Fact]
    public async Task RegenerateSummaryAsync_HandsBackTheIdItQueuedTheRequestUnder()
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

        var redis = new Mock<IRedisStateRepository>();
        Dictionary<string, string>? published = null;
        redis
            .Setup(item => item.StreamAddAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()))
            .Callback((string _, Dictionary<string, string> fields) => published = fields)
            .ReturnsAsync("1-0");

        var service = CreateServiceForRoom(room, redis, new Mock<IArtifactsFinalizationQueue>());
        var result = await service.RegenerateSummaryAsync(room.Id, hostId, "traceable", null, "Bearer t");

        Assert.True(result.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(result.Value));
        // The SAME id, not merely some id: the consumer files the outcome under what went out.
        Assert.Equal(published!["request_id"], result.Value);
    }

    /// <summary>
    /// An absent key means the answer has not arrived — never that it arrived and said yes.
    ///
    /// Redis runs allkeys-lru and the outcome carries a TTL, so "nothing there" covers both a
    /// rewrite still running and an outcome nobody collected in time. Reading it as success
    /// would turn every eviction into a silent claim that the rewrite worked, which is the exact
    /// failure this endpoint exists to remove.
    /// </summary>
    [Fact]
    public async Task GetSummaryRewriteStatusAsync_ReadsAnAbsentOutcomeAsPendingNotAsSuccess()
    {
        var hostId = Guid.NewGuid();
        var room = CreateEndedRoom(hostId);
        var redis = new Mock<IRedisStateRepository>();
        redis.Setup(item => item.StringGetAsync(It.IsAny<string>())).ReturnsAsync((string?)null);

        var service = CreateServiceForRoom(room, redis, new Mock<IArtifactsFinalizationQueue>());
        var result = await service.GetSummaryRewriteStatusAsync(room.Id, hostId, Guid.NewGuid().ToString());

        Assert.True(result.IsSuccess);
        Assert.Equal("pending", result.Value!.Status);
        Assert.Null(result.Value.Error);
    }

    /// <summary>
    /// The reason reaches the requester in the words the worker wrote, not as a category.
    /// </summary>
    [Fact]
    public async Task GetSummaryRewriteStatusAsync_CarriesTheWorkersOwnReason()
    {
        var hostId = Guid.NewGuid();
        var room = CreateEndedRoom(hostId);
        var requestId = Guid.NewGuid().ToString();
        var redis = new Mock<IRedisStateRepository>();
        redis
            .Setup(item => item.StringGetAsync(
                TranslationRoomConstants.SummaryRewriteStatusKeyPrefix + requestId))
            .ReturnsAsync("{\"Status\":\"failed\",\"Error\":\"This meeting has no saved transcript to summarise.\"}");

        var service = CreateServiceForRoom(room, redis, new Mock<IArtifactsFinalizationQueue>());
        var result = await service.GetSummaryRewriteStatusAsync(room.Id, hostId, requestId);

        Assert.True(result.IsSuccess);
        Assert.Equal("failed", result.Value!.Status);
        Assert.Equal("This meeting has no saved transcript to summarise.", result.Value.Error);
    }

    /// <summary>
    /// The failure text quotes what the worker found in this meeting's transcript, so it is
    /// behind the same gate as reading the artifacts themselves.
    /// </summary>
    [Fact]
    public async Task GetSummaryRewriteStatusAsync_RefusesSomebodyWithNoAccessToTheRoom()
    {
        var room = CreateEndedRoom(Guid.NewGuid());
        var redis = new Mock<IRedisStateRepository>();

        var service = CreateServiceForRoom(room, redis, new Mock<IArtifactsFinalizationQueue>());
        var result = await service.GetSummaryRewriteStatusAsync(room.Id, Guid.NewGuid(), Guid.NewGuid().ToString());

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Unauthorized, result.ErrorCode);
        redis.Verify(item => item.StringGetAsync(It.IsAny<string>()), Times.Never);
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
