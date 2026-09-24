using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.LanguagePolicy;
using WarpTalk.TranslationRoomService.Application.Mappers;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Domain.ValueObjects;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// WT-826: a meeting's record is published to the people who took part when it ends, unless the
/// host turned "Share the record with participants automatically" off.
///
/// Before this, every room was born HOST_ONLY and stayed there until the host pressed Publish on
/// a meeting that was already over — which, in practice, nobody did. Participants who had just
/// sat through a meeting opened its Recap and read "The host has not shared this meeting's record
/// yet", and its recording sat behind a consent hold only the host could lift.
/// </summary>
public class RecordAutoShareTests
{
    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid HostId = Guid.NewGuid();

    private readonly Mock<ITranslationRoomRepository> _rooms = new();
    private readonly Mock<ITranslationRoomArtifactRepository> _artifacts = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly List<TranslationRoomArtifact> _artifactRows = new();
    private readonly WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService _service;

    public RecordAutoShareTests()
    {
        var participants = new Mock<ITranslationRoomParticipantRepository>();
        participants.Setup(p => p.GetByRoomIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomParticipant>());

        _artifacts.Setup(repo => repo.FindAsync(
                It.IsAny<Expression<Func<TranslationRoomArtifact, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<TranslationRoomArtifact, bool>> predicate, string _, CancellationToken _) =>
                _artifactRows.FindAll(new Predicate<TranslationRoomArtifact>(predicate.Compile())));

        _unitOfWork.Setup(u => u.TranslationRoomRepository).Returns(_rooms.Object);
        _unitOfWork.Setup(u => u.TranslationRoomParticipantRepository).Returns(participants.Object);
        _unitOfWork.Setup(u => u.TranslationRoomSessionRepository).Returns(new Mock<ITranslationRoomSessionRepository>().Object);
        _unitOfWork.Setup(u => u.TranslationRoomArtifactRepository).Returns(_artifacts.Object);

        _service = new WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService(
            _unitOfWork.Object,
            new Mock<ILanguagePolicy>().Object,
            new Mock<IAudioRouteEventProcessor>().Object,
            new Mock<ITranslationRoomAudioRouteService>().Object,
            new Mock<IUserSettingsDirectory>().Object,
            new Mock<IWorkspaceMeetingPolicy>().Object,
            new Mock<IWorkspaceMemberDirectory>().Object,
            new Mock<WarpTalk.Shared.Interfaces.IEmailService>().Object,
            new Mock<ILogger<WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService>>().Object,
            redisStateRepository: new Mock<IRedisStateRepository>().Object);
    }

    // ---- where a room is born ---------------------------------------------------------------

    [Fact]
    public void ANewRoom_IsCreatedSharedWithItsParticipants()
    {
        var settings = TranslationRoomMapper.ResolveSettings("INSTANT", null);

        Assert.Equal(ArtifactAccessLevels.AllParticipants, settings.ArtifactAccess);
        // Written, not left absent: absence is reserved for rooms that predate the toggle.
        Assert.True(settings.AutoShareRecord);
    }

    [Fact]
    public void AHostWhoTurnsTheToggleOff_GetsAPrivateRoom()
    {
        var settings = TranslationRoomMapper.ResolveSettings("INSTANT", new RoomSettingsRequest(AutoShareRecord: false));

        Assert.Equal(ArtifactAccessLevels.HostOnly, settings.ArtifactAccess);
        Assert.False(settings.AutoShareRecord);
    }

    [Fact]
    public void ARoomThatPredatesTheToggle_ReadsAsOn()
    {
        // What every existing room's blob looks like: HOST_ONLY stored as a default, no toggle.
        var read = TranslationRoomMapper.ReadSettings("{\"requires_approval\":true,\"artifact_access\":\"HOST_ONLY\"}");

        Assert.True(read.AutoShareRecord, "a room created before WT-826 is shared when it ends, like a new one");
        Assert.Equal(ArtifactAccessLevels.HostOnly, read.ArtifactAccess);
    }

    // ---- ending the meeting ------------------------------------------------------------------

    [Fact]
    public async Task EndingARoomThatPredatesTheToggle_PublishesItsRecordAndReleasesTheRecording()
    {
        var room = GivenRoom("IN_PROGRESS", "{\"requires_approval\":true,\"artifact_access\":\"HOST_ONLY\"}");
        var recording = GivenRecording(consentRequired: true);

        var result = await _service.EndTranslationRoomAsync(RoomId, HostId);

        Assert.True(result.IsSuccess, result.Error);
        var settings = Read(room);
        Assert.Equal(ArtifactAccessLevels.AllParticipants, settings.ArtifactAccess);
        // Resolved to an explicit TRUE, so a recording arriving later can tell this room was
        // published by the rule from one that ended before it existed.
        Assert.True(settings.AutoShareRecord);
        Assert.False(recording.ConsentRequired, "auto-publish counts as the host releasing the recording");
        // Everything else in the blob survives.
        Assert.True(settings.RequiresApproval);
    }

    [Fact]
    public async Task EndingARoomWithTheToggleOff_KeepsTheRecordPrivateAndTheRecordingHeld()
    {
        var room = GivenRoom("IN_PROGRESS", "{\"artifact_access\":\"HOST_ONLY\",\"auto_share_record\":false}");
        var recording = GivenRecording(consentRequired: true);

        var result = await _service.EndTranslationRoomAsync(RoomId, HostId);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(ArtifactAccessLevels.HostOnly, Read(room).ArtifactAccess);
        Assert.False(Read(room).AutoShareRecord);
        Assert.True(recording.ConsentRequired);
    }

    [Fact]
    public async Task ADeletedRecording_IsNotReleasedByTheEnd()
    {
        GivenRoom("IN_PROGRESS", "{}");
        var deleted = GivenRecording(consentRequired: true);
        deleted.DeletedAt = DateTime.UtcNow;

        await _service.EndTranslationRoomAsync(RoomId, HostId);

        Assert.True(deleted.ConsentRequired);
    }

    // ---- the host's own Publish / Unpublish -------------------------------------------------

    /// <summary>
    /// The trap: an existing room already STORES HOST_ONLY (as a default), so "keep it private"
    /// sends the level it already has. Treating that as a no-op would drop the one thing the host
    /// said, and the end of the meeting would then publish it anyway.
    /// </summary>
    [Fact]
    public async Task KeepingALiveRoomPrivate_SurvivesTheEndOfTheMeeting()
    {
        var room = GivenRoom("IN_PROGRESS", "{\"artifact_access\":\"HOST_ONLY\"}");

        var kept = await _service.SetArtifactAccessAsync(RoomId, HostId, ArtifactAccessLevels.HostOnly);
        Assert.True(kept.IsSuccess, kept.Error);
        Assert.False(Read(room).AutoShareRecord);

        await _service.EndTranslationRoomAsync(RoomId, HostId);

        Assert.Equal(ArtifactAccessLevels.HostOnly, Read(room).ArtifactAccess);
    }

    [Fact]
    public async Task UnpublishingAfterTheEnd_StillWorks()
    {
        var room = GivenRoom("IN_PROGRESS", "{}");
        await _service.EndTranslationRoomAsync(RoomId, HostId);
        Assert.Equal(ArtifactAccessLevels.AllParticipants, Read(room).ArtifactAccess);

        var result = await _service.SetArtifactAccessAsync(RoomId, HostId, ArtifactAccessLevels.HostOnly);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(ArtifactAccessLevels.HostOnly, Read(room).ArtifactAccess);
    }

    // ---- harness -------------------------------------------------------------------------------

    private TranslationRoom GivenRoom(string status, string settings)
    {
        var room = new TranslationRoom
        {
            Id = RoomId,
            WorkspaceId = Guid.NewGuid(),
            HostId = HostId,
            TranslationRoomCode = "abc-defg-hij",
            Title = "Weekly sync",
            Status = status,
            TranslationRoomType = "INSTANT",
            SourceLanguage = "vi",
            TargetLanguages = "[\"en\"]",
            Settings = settings,
        };
        _rooms.Setup(r => r.GetByIdAsync(RoomId, It.IsAny<CancellationToken>())).ReturnsAsync(room);
        return room;
    }

    private TranslationRoomArtifact GivenRecording(bool consentRequired)
    {
        var artifact = new TranslationRoomArtifact
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = RoomId,
            ArtifactType = "OPTIONAL_RECORDING",
            Status = "PROCESSING",
            ConsentRequired = consentRequired,
        };
        _artifactRows.Add(artifact);
        return artifact;
    }

    private static TranslationRoomSettings Read(TranslationRoom room) =>
        JsonSerializer.Deserialize<TranslationRoomSettings>(
            room.Settings!,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
}
