using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// The name the download endpoint hands back, per artifact type.
///
/// Every artifact used to arrive as "warptalk-optional_recording-3f2a…b7.mp4" — the row's primary
/// key. Three recordings from three meetings landed in one Downloads folder as three hex strings,
/// and the only way back to "which of these is Tuesday's standup" was to open all three. These
/// tests pin the formula per type, and pin the two types that KEEP the id name on purpose.
/// </summary>
public sealed class RecapDownloadFileNameTests
{
    private static readonly DateTime Started = new(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ATranscriptIsNamedAfterItsMeeting()
    {
        var userId = Guid.NewGuid();
        var artifact = Recap(userId, "TRANSCRIPT_EXPORT");
        artifact.FileFormat = "markdown";
        artifact.Content = "# Transcript\n---\n**[Nam (VI)]**: xin chào";

        var result = await Service(artifact).GetArtifactDownloadAsync(artifact.Id, userId);

        Assert.True(result.IsSuccess);
        Assert.Equal("Họp sprint 42 - Transcript - 2026-09-18.txt", result.Value!.FileName);
    }

    [Fact]
    public async Task ASummaryIsNamedAfterItsMeetingAndSaysWhichLanguageItIsIn()
    {
        // A room can hold the same summary in several languages (WT-703). The name has to say
        // which one this file is, or the second download overwrites the first.
        var userId = Guid.NewGuid();
        var artifact = Recap(userId, "SUMMARY_EXPORT");
        artifact.FileFormat = "json";
        artifact.Content = """{"templateKey":"general","summaryLanguage":"ja","summary":"…"}""";

        var result = await Service(artifact).GetArtifactDownloadAsync(artifact.Id, userId);

        Assert.True(result.IsSuccess);
        Assert.Equal("Họp sprint 42 - Summary (JA) - 2026-09-18.txt", result.Value!.FileName);
    }

    [Fact]
    public async Task ASummaryWithNoLanguageStampIsJustTheSummary()
    {
        // Summaries written before the language picker existed have no stamp. They were in the
        // language that was spoken, and "Summary ()" would be a claim about nothing.
        var userId = Guid.NewGuid();
        var artifact = Recap(userId, "SUMMARY_EXPORT");
        artifact.FileFormat = "json";
        artifact.Content = """{"summary":"…"}""";

        var result = await Service(artifact).GetArtifactDownloadAsync(artifact.Id, userId);

        Assert.True(result.IsSuccess);
        Assert.Equal("Họp sprint 42 - Summary - 2026-09-18.txt", result.Value!.FileName);
    }

    [Fact]
    public async Task ARecordingIsNamedAfterItsMeetingAndKeepsItsContainer()
    {
        var userId = Guid.NewGuid();
        var artifact = Recap(userId, "OPTIONAL_RECORDING");
        artifact.FileFormat = "mp4";
        artifact.FileUrl = "https://storage.example/rooms/abc/egress-1.mp4";
        artifact.ContainsRawAudio = true;

        var result = await Service(artifact).GetArtifactDownloadAsync(artifact.Id, userId);

        Assert.True(result.IsSuccess);
        Assert.Equal("Họp sprint 42 - Recording - 2026-09-18.mp4", result.Value!.FileName);
    }

    [Fact]
    public async Task ASecondRecordingOfTheSameMeetingSaysWhichPassItIs()
    {
        // A host who stops and restarts recording gets a second row with the same title and date.
        // The ordinal counts in RECORDING order, not row order: egress finishes out of order.
        var userId = Guid.NewGuid();
        var first = Recap(userId, "OPTIONAL_RECORDING");
        first.RecordingStartedAt = Started.AddMinutes(5);
        first.CreatedAt = Started.AddHours(2);

        var second = new TranslationRoomArtifact
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = first.TranslationRoomId,
            TranslationRoom = first.TranslationRoom,
            ArtifactType = "OPTIONAL_RECORDING",
            Status = "COMPLETED",
            FileFormat = "mp4",
            FileUrl = "https://storage.example/rooms/abc/egress-2.mp4",
            ContainsRawAudio = true,
            // Started later, finished FIRST — which is exactly the pair CreatedAt would misorder.
            RecordingStartedAt = Started.AddMinutes(40),
            CreatedAt = Started.AddMinutes(45)
        };
        first.FileFormat = "mp4";
        first.FileUrl = "https://storage.example/rooms/abc/egress-1.mp4";
        first.ContainsRawAudio = true;

        var firstResult = await Service(first, second).GetArtifactDownloadAsync(first.Id, userId);
        var secondResult = await Service(second, first).GetArtifactDownloadAsync(second.Id, userId);

        Assert.Equal("Họp sprint 42 - Recording - 2026-09-18.mp4", firstResult.Value!.FileName);
        Assert.Equal("Họp sprint 42 - Recording (2) - 2026-09-18.mp4", secondResult.Value!.FileName);
    }

    [Fact]
    public async Task ADebugLogKeepsTheRowIdName()
    {
        // Nobody files a debug log away. It is fetched by an engineer who already has the id in
        // hand, and for them the id IS the useful name.
        var userId = Guid.NewGuid();
        var artifact = Recap(userId, "DEBUG_LOG");
        artifact.FileFormat = "markdown";
        artifact.Content = "# Raw notes";

        var result = await Service(artifact).GetArtifactDownloadAsync(artifact.Id, userId);

        Assert.True(result.IsSuccess);
        Assert.Equal($"warptalk-debug_log-{artifact.Id:N}.md", result.Value!.FileName);
    }

    [Fact]
    public async Task AnUntitledMeetingStillProducesAWritableName()
    {
        var userId = Guid.NewGuid();
        var artifact = Recap(userId, "TRANSCRIPT_EXPORT");
        artifact.TranslationRoom.Title = null!;
        artifact.Content = "something";

        var result = await Service(artifact).GetArtifactDownloadAsync(artifact.Id, userId);

        Assert.True(result.IsSuccess);
        Assert.Equal("Meeting - Transcript - 2026-09-18.txt", result.Value!.FileName);
    }

    /// <summary>
    /// The player and the download share this endpoint, so the disposition is the caller's choice.
    /// An attachment on the link the record page puts in its &lt;video&gt; element is a browser
    /// being told to save the file it was asked to play.
    /// </summary>
    [Fact]
    public async Task OnlyADownloadAsksTheSignedLinkToAttachAName()
    {
        var userId = Guid.NewGuid();
        var artifact = Recap(userId, "OPTIONAL_RECORDING");
        artifact.FileFormat = "mp4";
        artifact.FileUrl = "https://storage.example/rooms/abc/egress-1.mp4";
        artifact.ContainsRawAudio = true;

        var names = new List<string?>();
        var service = Service(new[] { artifact }, names);

        await service.GetArtifactDownloadAsync(artifact.Id, userId);
        await service.GetArtifactDownloadAsync(artifact.Id, userId, asAttachment: true);

        Assert.Equal(
            new string?[] { null, "Họp sprint 42 - Recording - 2026-09-18.mp4" },
            names);
    }

    private static TranslationRoomArtifact Recap(Guid hostId, string artifactType)
    {
        var room = new TranslationRoom
        {
            Id = Guid.NewGuid(),
            HostId = hostId,
            Title = "Họp sprint 42",
            StartedAt = Started,
            Settings = "{}"
        };
        return new TranslationRoomArtifact
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = room.Id,
            TranslationRoom = room,
            ArtifactType = artifactType,
            Status = "COMPLETED"
        };
    }

    private static TranslationRoomArtifactService Service(params TranslationRoomArtifact[] roomArtifacts) =>
        Service(roomArtifacts, null);

    /// <param name="signedNames">
    /// Collects the file name each signing asked for — null when the caller wanted the object as
    /// stored.
    /// </param>
    private static TranslationRoomArtifactService Service(
        IReadOnlyList<TranslationRoomArtifact> roomArtifacts,
        List<string?>? signedNames)
    {
        var repository = new Mock<ITranslationRoomArtifactRepository>();
        foreach (var item in roomArtifacts)
        {
            repository
                .Setup(repo => repo.GetArtifactWithRoomAsync(item.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(item);
        }
        repository
            .Setup(repo => repo.GetArtifactsByRoomIdAsync(
                roomArtifacts[0].TranslationRoomId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(roomArtifacts.ToList());

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(work => work.TranslationRoomArtifactRepository).Returns(repository.Object);

        var signer = new Mock<IArtifactUrlSigner>();
        signer
            .Setup(item => item.CreateDownloadUrlAsync(
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, TimeSpan _, string? name, CancellationToken _) =>
            {
                signedNames?.Add(name);
                return url;
            });

        return new TranslationRoomArtifactService(
            unitOfWork.Object,
            NullLogger<TranslationRoomArtifactService>.Instance,
            signer.Object,
            new Mock<IRedisStateRepository>().Object,
            new Mock<IArtifactsFinalizationQueue>().Object,
            SummaryLanguageVariantTests.AllowingPolicy().Object);
    }
}
