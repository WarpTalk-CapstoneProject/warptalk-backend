using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.LanguagePolicy;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Domain.ValueObjects;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// Sharing a finished meeting's record — its transcript, AI summary and recording. WT-480.
///
/// The visibility axis. It is NOT finalizing: Finalize locks the wording so no more corrections
/// land, this decides who may read it, and the two stay independent so a typo can still be fixed
/// after the record has been shared.
/// </summary>
public class ShareMeetingRecordTests
{
    private readonly Mock<ITranslationRoomRepository> _mockRoomRepo = new();
    private readonly Mock<IUnitOfWork> _mockUnitOfWork = new();
    private readonly WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService _service;

    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid HostId = Guid.NewGuid();

    public ShareMeetingRecordTests()
    {
        _mockUnitOfWork.Setup(u => u.TranslationRoomRepository).Returns(_mockRoomRepo.Object);

        _service = new WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService(
            _mockUnitOfWork.Object,
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

    private TranslationRoom GivenRoom(string status, Guid? hostId = null, string? settings = null)
    {
        var room = new TranslationRoom
        {
            Id = RoomId,
            WorkspaceId = Guid.NewGuid(),
            HostId = hostId ?? HostId,
            TranslationRoomCode = "erp-gyjn-qfe",
            Title = "QBR",
            Status = status,
            TranslationRoomType = "INSTANT",
            SourceLanguage = "vi",
            TargetLanguages = "[\"en\"]",
            Settings = settings ?? "{}",
        };

        _mockRoomRepo
            .Setup(repository => repository.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        return room;
    }

    /// <summary>Settings are stored snake_case — see the [JsonPropertyName] attributes.</summary>
    private static string ArtifactAccessOf(TranslationRoom room) =>
        JsonSerializer.Deserialize<TranslationRoomSettings>(
            room.Settings,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.ArtifactAccess!;

    [Fact]
    public async Task SetArtifactAccess_SharesTheRecord_OfAMeetingThatHasAlreadyENDED()
    {
        // The whole ticket, in one test. UpdateTranslationRoomSettingsAsync — the other writer of
        // this same field — refuses anything past WAITING with ErrorSettingsLocked. Routing this
        // through it would have made the feature refuse in the ONLY state it is ever used in,
        // because a record cannot be shared before the artifacts it shares exist.
        var room = GivenRoom("ENDED");

        var result = await _service.SetArtifactAccessAsync(RoomId, HostId, ArtifactAccessLevels.AllParticipants);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(ArtifactAccessLevels.AllParticipants, ArtifactAccessOf(room));
    }

    [Fact]
    public async Task SetArtifactAccess_TakesTheRecordBack()
    {
        // Unpublish. The host shared it, then thought better of it.
        var room = GivenRoom("ENDED", settings: "{\"artifact_access\":\"ALL_PARTICIPANTS\"}");

        var result = await _service.SetArtifactAccessAsync(RoomId, HostId, ArtifactAccessLevels.HostOnly);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(ArtifactAccessLevels.HostOnly, ArtifactAccessOf(room));
    }

    [Fact]
    public async Task SetArtifactAccess_RefusesAnyoneWhoIsNotTheHost()
    {
        // Host only, matching FinalizeTranscriptAsync. A workspace Admin who may administer the
        // workspace is still not the person who ran this meeting.
        GivenRoom("ENDED");

        var result = await _service.SetArtifactAccessAsync(RoomId, Guid.NewGuid(), ArtifactAccessLevels.AllParticipants);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Unauthorized, result.ErrorCode);
    }

    [Fact]
    public async Task SetArtifactAccess_RefusesALevelTheGuardCannotEnforce()
    {
        // An unrecognised level reads as HOST_ONLY at the guard, so storing one would silently
        // deny everybody while the screen claimed the record was shared.
        var room = GivenRoom("ENDED");

        var result = await _service.SetArtifactAccessAsync(RoomId, HostId, "WORKSPACE");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Equal("{}", room.Settings);
    }

    [Fact]
    public async Task SetArtifactAccess_KeepsEverySettingItWasNotAskedToChange()
    {
        // A PATCH of one field. Sharing a record must not quietly re-open a locked room or turn
        // recording on, which is what serializing a fresh settings object would do.
        var room = GivenRoom(
            "ENDED",
            settings: "{\"requires_approval\":true,\"mute_on_entry\":true,\"artifact_access\":\"HOST_ONLY\"}");

        var result = await _service.SetArtifactAccessAsync(RoomId, HostId, ArtifactAccessLevels.AllParticipants);

        Assert.True(result.IsSuccess, result.Error);
        var settings = JsonSerializer.Deserialize<TranslationRoomSettings>(
            room.Settings,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(ArtifactAccessLevels.AllParticipants, settings.ArtifactAccess);
        Assert.True(settings.RequiresApproval);
        Assert.True(settings.MuteOnEntry);
    }

    [Fact]
    public async Task SetArtifactAccess_OnMalformedSettings_DoesNotThrow()
    {
        // Same direction ArtifactAccessHelper fails in: unreadable settings resolve to defaults
        // rather than a 500.
        var room = GivenRoom("ENDED", settings: "{not json");

        var result = await _service.SetArtifactAccessAsync(RoomId, HostId, ArtifactAccessLevels.AllParticipants);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(ArtifactAccessLevels.AllParticipants, ArtifactAccessOf(room));
    }

    [Fact]
    public async Task SetArtifactAccess_IsIdempotent()
    {
        // The button can legitimately be pressed twice — from the banner and from the tab.
        var room = GivenRoom("ENDED", settings: "{\"artifact_access\":\"ALL_PARTICIPANTS\"}");

        var result = await _service.SetArtifactAccessAsync(RoomId, HostId, ArtifactAccessLevels.AllParticipants);

        Assert.True(result.IsSuccess, result.Error);
        _mockUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetArtifactAccess_RefusesARoomThatDoesNotExist()
    {
        _mockRoomRepo
            .Setup(repository => repository.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoom?)null);

        var result = await _service.SetArtifactAccessAsync(RoomId, HostId, ArtifactAccessLevels.AllParticipants);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
    }
}
