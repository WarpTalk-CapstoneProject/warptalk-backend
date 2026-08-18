using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.LanguagePolicy;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// What the pre-join screen is told it may offer, for one room code.
///
/// WT-490: a room configured with two languages showed four in the picker, because this endpoint
/// only ever reported the WORKSPACE's policy. The room's own declared set — the thing that decides
/// who will actually be speaking — was never sent, so the screen had nothing to narrow by and a
/// joiner could pick a language nobody in the room would ever use.
///
/// The two limits stay separate on the wire. Intersecting them here would collapse "unrestricted"
/// (an empty list, which is what an unknown or half-typed code answers with) into "offer nothing",
/// and a pre-join screen that offers nothing cannot be filled in at all.
/// </summary>
public class JoinLanguagePolicyTests
{
    private readonly Mock<ITranslationRoomRepository> _mockRoomRepo = new();
    private readonly Mock<IWorkspaceMeetingPolicy> _mockWorkspaceMeetingPolicy = new();
    private readonly WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService _roomService;

    public JoinLanguagePolicyTests()
    {
        var mockUnitOfWork = new Mock<IUnitOfWork>();
        mockUnitOfWork.Setup(u => u.TranslationRoomRepository).Returns(_mockRoomRepo.Object);

        _roomService = new WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService(
            mockUnitOfWork.Object,
            new Mock<ILanguagePolicy>().Object,
            new Mock<IAudioRouteEventProcessor>().Object,
            new Mock<ITranslationRoomAudioRouteService>().Object,
            new Mock<IUserSettingsDirectory>().Object,
            _mockWorkspaceMeetingPolicy.Object,
            new Mock<IWorkspaceMemberDirectory>().Object,
            new Mock<WarpTalk.Shared.Interfaces.IEmailService>().Object,
            new Mock<ILogger<WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService>>().Object,
            redisStateRepository: new Mock<IRedisStateRepository>().Object);
    }

    private void GivenRoom(string sourceLanguage, List<string> targetLanguages, Guid? workspaceId = null)
    {
        _mockRoomRepo
            .Setup(repository => repository.GetByCodeAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationRoom
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId ?? Guid.NewGuid(),
                TranslationRoomCode = "ktw-xcag-bcr",
                Title = "QBR",
                Status = "SCHEDULED",
                TranslationRoomType = "INSTANT",
                SourceLanguage = sourceLanguage,
                TargetLanguages = LanguageHelper.SerializeTargetLanguages(targetLanguages),
                Settings = "{}",
            });
    }

    private void GivenWorkspaceAllows(params string[] languages)
    {
        _mockWorkspaceMeetingPolicy
            .Setup(policy => policy.GetAllowedLanguagesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<string>>(languages));
    }

    [Fact]
    public async Task GetJoinLanguagePolicy_ReportsTheRoomsOwnLanguages_NotOnlyTheWorkspacePolicy()
    {
        // The reported case: workspace permits four, room declares two, picker offered four.
        GivenRoom("vi-VN", new List<string> { "en-US" });
        GivenWorkspaceAllows("vi", "en", "ja", "ko");

        var result = await _roomService.GetJoinLanguagePolicyByCodeAsync("ktw-xcag-bcr");

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "vi", "en", "ja", "ko" }, result.Value!.AllowedTargetLanguages);
        Assert.Equal(new[] { "vi", "en" }, result.Value.RoomLanguages);
    }

    [Fact]
    public async Task GetJoinLanguagePolicy_CountsTheSourceLanguageAsOneOfTheRooms()
    {
        // A room is defined by the set of languages spoken in it, and the host's own source
        // language is one of them — omitting it would refuse the host their own language.
        GivenRoom("ja-JP", new List<string> { "en-US" });
        GivenWorkspaceAllows();

        var result = await _roomService.GetJoinLanguagePolicyByCodeAsync("ktw-xcag-bcr");

        Assert.Contains("ja", result.Value!.RoomLanguages);
        Assert.Contains("en", result.Value.RoomLanguages);
    }

    [Fact]
    public async Task GetJoinLanguagePolicy_DoesNotRepeatALanguageThatIsBothSourceAndTarget()
    {
        GivenRoom("vi-VN", new List<string> { "vi-VN", "en-US" });
        GivenWorkspaceAllows();

        var result = await _roomService.GetJoinLanguagePolicyByCodeAsync("ktw-xcag-bcr");

        Assert.Equal(new[] { "vi", "en" }, result.Value!.RoomLanguages);
    }

    [Fact]
    public async Task GetJoinLanguagePolicy_AnswersUnrestricted_ForACodeThatResolvesToNothing()
    {
        // Called on every keystroke while a code is being typed, so a half-typed code must not
        // paint an error — and must not become a way to probe which codes exist.
        _mockRoomRepo
            .Setup(repository => repository.GetByCodeAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoom?)null);

        var result = await _roomService.GetJoinLanguagePolicyByCodeAsync("ktw");

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.AllowedTargetLanguages);
        Assert.Empty(result.Value.RoomLanguages);
    }

    [Fact]
    public async Task GetJoinLanguagePolicy_StillReportsTheRoomsLanguages_WhenTheWorkspacePolicyLookupFails()
    {
        // Fails OPEN on the workspace side only. A WorkspaceService blip must not also erase the
        // room's own set: that set is already in hand, and dropping it would re-open WT-490 for
        // exactly as long as the outage lasts.
        GivenRoom("vi-VN", new List<string> { "en-US" });
        _mockWorkspaceMeetingPolicy
            .Setup(policy => policy.GetAllowedLanguagesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<IReadOnlyList<string>>("workspace service unavailable"));

        var result = await _roomService.GetJoinLanguagePolicyByCodeAsync("ktw-xcag-bcr");

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.AllowedTargetLanguages);
        Assert.Equal(new[] { "vi", "en" }, result.Value.RoomLanguages);
    }
}
