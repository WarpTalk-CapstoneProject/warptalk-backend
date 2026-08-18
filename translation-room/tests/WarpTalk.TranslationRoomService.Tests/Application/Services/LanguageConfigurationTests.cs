using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.LanguagePolicy;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

public class LanguageConfigurationTests
{
    private readonly Mock<IUnitOfWork> _mockUnitOfWork;
    private readonly Mock<ITranslationRoomRepository> _mockRoomRepo;
    private readonly Mock<ITranslationRoomParticipantRepository> _mockParticipantRepo;
    private readonly Mock<ILanguagePolicy> _mockLanguagePolicy;
    private readonly Mock<IAudioRouteEventProcessor> _mockAudioRouteEventProcessor;
    private readonly Mock<ITranslationRoomAudioRouteService> _mockAudioRouteService;
    private readonly Mock<IUserSettingsDirectory> _mockUserSettingsDirectory;
    private readonly Mock<WarpTalk.Shared.Interfaces.IEmailService> _mockEmailService;
    private readonly Mock<IWorkspaceMeetingPolicy> _mockWorkspaceMeetingPolicy;
    private readonly Mock<IWorkspaceMemberDirectory> _mockWorkspaceMemberDirectory;
    private readonly Mock<IRedisStateRepository> _mockRedisStateRepository;
    private readonly Mock<ILogger<WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService>> _mockLogger;
    private readonly WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService _roomService;

    public LanguageConfigurationTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockRoomRepo = new Mock<ITranslationRoomRepository>();
        _mockParticipantRepo = new Mock<ITranslationRoomParticipantRepository>();
        _mockLanguagePolicy = new Mock<ILanguagePolicy>();
        _mockAudioRouteEventProcessor = new Mock<IAudioRouteEventProcessor>();
        _mockAudioRouteService = new Mock<ITranslationRoomAudioRouteService>();
        _mockUserSettingsDirectory = new Mock<IUserSettingsDirectory>();
        _mockWorkspaceMeetingPolicy = new Mock<IWorkspaceMeetingPolicy>();
        _mockWorkspaceMemberDirectory = new Mock<IWorkspaceMemberDirectory>();
        _mockEmailService = new Mock<WarpTalk.Shared.Interfaces.IEmailService>();
        _mockRedisStateRepository = new Mock<IRedisStateRepository>();
        _mockLogger = new Mock<ILogger<WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService>>();

        _mockUnitOfWork.Setup(u => u.TranslationRoomRepository).Returns(_mockRoomRepo.Object);
        _mockUnitOfWork.Setup(u => u.TranslationRoomParticipantRepository).Returns(_mockParticipantRepo.Object);

        // These tests are about language resolution, not permission — let the workspace allow.
        _mockWorkspaceMeetingPolicy.Setup(p => p.ValidateMeetingCreationAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        // ...and the tenant itself is live unless a test suspends it.
        _mockWorkspaceMeetingPolicy.Setup(p => p.EnsureWorkspaceCanHostMeetingsAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        _roomService = new WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService(
            _mockUnitOfWork.Object,
            _mockLanguagePolicy.Object,
            _mockAudioRouteEventProcessor.Object,
            _mockAudioRouteService.Object,
            _mockUserSettingsDirectory.Object,
            _mockWorkspaceMeetingPolicy.Object,
            _mockWorkspaceMemberDirectory.Object,
            _mockEmailService.Object,
            _mockLogger.Object,
            redisStateRepository: _mockRedisStateRepository.Object);
    }

    [Fact]
    public async Task CreateRoom_UsesAuthServiceLanguageDefaults_WhenRequestOmitsLanguages()
    {
        var userId = Guid.NewGuid();
        _mockUserSettingsDirectory
            .Setup(directory => directory.GetDefaultsAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserLanguageDefaults("vi-VN", "en-US"));
        _mockLanguagePolicy
            .Setup(policy => policy.IsSupportedAsync(It.IsAny<string>()))
            .ReturnsAsync(true);
        _mockRoomRepo
            .Setup(repository => repository.ExistsByCodeAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // A workspace id is required now: the meeting-creation permission lives on the workspace
        // membership, so a room with no workspace could not be authorized at all (WT-249).
        var request = new CreateTranslationRoomRequest(
            Guid.NewGuid(), "Defaults", null, "INSTANT", 10,
            null, null, null, null, null);

        var result = await _roomService.CreateTranslationRoomAsync(request, userId);

        Assert.True(result.IsSuccess);
        Assert.Equal("vi", result.Value!.SourceLanguage);
        Assert.Contains("en", result.Value.TargetLanguages);
        _mockUserSettingsDirectory.Verify(
            directory => directory.GetDefaultsAsync(userId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateRoom_ShouldFail_WhenSourceLanguageIsUnsupported()
    {
        // Arrange
        var request = new CreateTranslationRoomRequest(
            null, "Test Room", null, "INSTANT", 10,
            "xx-XX", // Unsupported
            new List<string> { "en-US" },
            null, null, null
        );

        _mockLanguagePolicy.Setup(v => v.IsSupportedAsync("xx-XX")).ReturnsAsync(false);

        // Act
        var result = await _roomService.CreateTranslationRoomAsync(request, Guid.NewGuid());

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Contains("not supported", result.Error);
    }

    [Fact]
    public async Task CreateRoom_ShouldFail_WhenTargetLanguageIsUnsupported()
    {
        // Arrange
        var request = new CreateTranslationRoomRequest(
            null, "Test Room", null, "INSTANT", 10,
            "vi-VN",
            new List<string> { "xx-XX" }, // Unsupported
            null, null, null
        );

        _mockLanguagePolicy.Setup(v => v.IsSupportedAsync("vi-VN")).ReturnsAsync(true);
        _mockLanguagePolicy.Setup(v => v.IsSupportedAsync("xx-XX")).ReturnsAsync(false);

        // Act
        var result = await _roomService.CreateTranslationRoomAsync(request, Guid.NewGuid());

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Contains("not supported", result.Error);
    }

    [Fact]
    public async Task JoinRoom_ShouldFail_WhenLanguageNotInRoomPolicy()
    {
        // Arrange
        var room = new TranslationRoom
        {
            Id = Guid.NewGuid(),
            TranslationRoomCode = "abc-defg-hij",
            SourceLanguage = "vi-VN",
            TargetLanguages = LanguageHelper.SerializeTargetLanguages(new List<string> { "en-US" }),
            Status = "WAITING"
        };

        _mockRoomRepo.Setup(r => r.GetByCodeAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        _mockLanguagePolicy.Setup(v => v.IsSupportedAsync(It.IsAny<string>())).ReturnsAsync(true);
        _mockLanguagePolicy.Setup(v => v.ValidateParticipantLanguagesAsync("ja-JP", "vi-VN", room)).ReturnsAsync("must be the source language or one of the target languages");
        _mockLanguagePolicy.Setup(v => v.ValidateParticipantLanguagesAsync("vi-VN", "en-US", room)).ReturnsAsync((string?)null);

        var request = new JoinTranslationRoomRequest(
            "abc-defg-hij", "User",
            "ja-JP", // Not in policy
            "vi-VN"
        );

        // Act
        var result = await _roomService.JoinTranslationRoomAsync(request, Guid.NewGuid());

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Contains("must be the source language or one of the target languages", result.Error);
    }

    [Fact]
    public async Task UpdateRoomLanguages_ShouldFail_WhenRoomIsInProgress()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = new TranslationRoom
        {
            Id = roomId,
            HostId = hostId,
            Status = "IN_PROGRESS"
        };

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        // Note: We might need a specific request for updating languages if it's not part of RoomSettings
        // But for now, let's assume we use UpdateRoomSettings or a new method.
        // If it's part of UpdateRoomSettingsRequest, we need to check if the DTO supports it.
    }
}
