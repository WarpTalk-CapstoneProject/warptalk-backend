using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// WT-824 — the recording consent hold, from the side of the people it was blocking.
///
/// Every recording row is written with <c>ConsentRequired = true</c>. The hold exists so that raw
/// audio and video of everyone in the meeting is not handed to each participant before the host
/// releases it. It was never meant to stop the HOST — yet the download check refused the host too,
/// so the host's own recording was reachable only through a second, separate "grant consent" call
/// that the host had to make to themselves. And after a Transfer Host that call was refused for the
/// booker (the one person the artifact policy always admits), so nobody could both release the
/// recording and read it.
///
/// It also answered "Consent is required" for a recording that had no file yet, so a PROCESSING row
/// read as a permission problem instead of "not ready" — which is exactly how WT-824 was reported.
/// </summary>
public sealed class RecordingConsentHoldTests
{
    [Fact]
    public async Task TheHostDownloadsTheirOwnRecordingWithoutGrantingConsentFirst()
    {
        var host = Guid.NewGuid();
        var artifact = CompletedRecording(host);

        var result = await CreateService(artifact).GetArtifactDownloadAsync(artifact.Id, host);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(artifact.FileUrl, result.Value!.Url);
        // Reading is not releasing: the host looking at their recording must not open it to
        // everyone else as a side effect.
        Assert.True(artifact.ConsentRequired);
    }

    [Fact]
    public async Task TheBookerStillReadsTheRecordingAfterHandingTheRoomOver()
    {
        var booker = Guid.NewGuid();
        var artifact = CompletedRecording(booker);
        artifact.TranslationRoom.ActiveHostId = Guid.NewGuid();

        var result = await CreateService(artifact).GetArtifactDownloadAsync(artifact.Id, booker);

        Assert.True(result.IsSuccess, result.Error);
    }

    [Fact]
    public async Task AParticipantIsHeldUntilTheHostReleasesTheRecording()
    {
        var host = Guid.NewGuid();
        var participant = Guid.NewGuid();
        var artifact = CompletedRecording(host);
        OpenToParticipants(artifact, participant);

        var result = await CreateService(artifact).GetArtifactDownloadAsync(artifact.Id, participant);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Unauthorized, result.ErrorCode);
        Assert.Contains("Consent is required", result.Error);
    }

    /// <summary>
    /// A recording that is still being written has nothing behind the hold. Saying "consent is
    /// required" sends the reader to the host for a file that does not exist yet.
    /// </summary>
    [Fact]
    public async Task ARecordingStillProcessingSaysNotReadyRatherThanConsent()
    {
        var host = Guid.NewGuid();
        var participant = Guid.NewGuid();
        var artifact = CompletedRecording(host);
        artifact.Status = "PROCESSING";
        artifact.FileUrl = null;
        OpenToParticipants(artifact, participant);

        var result = await CreateService(artifact).GetArtifactDownloadAsync(artifact.Id, participant);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidState, result.ErrorCode);
        Assert.DoesNotContain("Consent", result.Error);
    }

    [Fact]
    public async Task TheBookerCanReleaseTheRecordingAfterATransfer()
    {
        var booker = Guid.NewGuid();
        var artifact = CompletedRecording(booker);
        artifact.TranslationRoom.ActiveHostId = Guid.NewGuid();

        var result = await CreateService(artifact).ApproveArtifactConsentAsync(artifact.Id, booker);

        Assert.True(result.IsSuccess, result.Error);
        Assert.False(artifact.ConsentRequired);
    }

    [Fact]
    public async Task TheCurrentHostCanStillReleaseTheRecording()
    {
        var booker = Guid.NewGuid();
        var transferee = Guid.NewGuid();
        var artifact = CompletedRecording(booker);
        artifact.TranslationRoom.ActiveHostId = transferee;

        var result = await CreateService(artifact).ApproveArtifactConsentAsync(artifact.Id, transferee);

        Assert.True(result.IsSuccess, result.Error);
        Assert.False(artifact.ConsentRequired);
    }

    [Fact]
    public async Task AParticipantStillCannotReleaseTheRecordingToThemselves()
    {
        var host = Guid.NewGuid();
        var participant = Guid.NewGuid();
        var artifact = CompletedRecording(host);
        OpenToParticipants(artifact, participant);

        var result = await CreateService(artifact).ApproveArtifactConsentAsync(artifact.Id, participant);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Unauthorized, result.ErrorCode);
        Assert.True(artifact.ConsentRequired);
    }

    private static TranslationRoomArtifact CompletedRecording(Guid hostId)
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
            ArtifactType = "OPTIONAL_RECORDING",
            Status = "COMPLETED",
            ConsentRequired = true,
            ContainsRawAudio = true,
            ContainsRawVideo = true,
            FileFormat = "mp4",
            FileUrl = "https://storage.example/recordings/room.mp4"
        };
    }

    /// <summary>
    /// Opens the room's outputs to participants, so the ONLY thing between this participant and the
    /// file is the consent hold — otherwise a refusal would prove nothing about consent.
    /// </summary>
    private static void OpenToParticipants(TranslationRoomArtifact artifact, Guid participant)
    {
        artifact.TranslationRoom.Settings = $"{{\"artifact_access\":\"{ArtifactAccessLevels.AllParticipants}\"}}";
        artifact.TranslationRoom.TranslationRoomParticipants.Add(new TranslationRoomParticipant
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = artifact.TranslationRoomId,
            UserId = participant,
            DisplayName = "Participant",
            Role = "PARTICIPANT",
            ListenLanguage = "en",
            SpeakLanguage = "en",
            Status = "LEFT"
        });
    }

    private static TranslationRoomArtifactService CreateService(TranslationRoomArtifact artifact)
    {
        var repository = new Mock<ITranslationRoomArtifactRepository>();
        repository
            .Setup(repo => repo.GetArtifactWithRoomAsync(artifact.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(artifact);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(work => work.TranslationRoomArtifactRepository).Returns(repository.Object);
        var signer = new Mock<IArtifactUrlSigner>();
        signer
            .Setup(item => item.CreateDownloadUrlAsync(
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, TimeSpan _, string? _, CancellationToken _) => url);
        return new TranslationRoomArtifactService(
            unitOfWork.Object,
            NullLogger<TranslationRoomArtifactService>.Instance,
            signer.Object,
            new Mock<IRedisStateRepository>().Object,
            new Mock<IArtifactsFinalizationQueue>().Object,
            SummaryLanguageVariantTests.AllowingPolicy().Object);
    }
}
