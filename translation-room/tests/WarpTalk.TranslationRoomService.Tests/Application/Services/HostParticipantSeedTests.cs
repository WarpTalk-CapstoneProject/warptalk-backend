using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.LanguagePolicy;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// WT-281. The WT-82 host auto-add seeded the row with placeholders: DisplayName was the literal
/// string "Host" (production really did show a participant called "Host"), and BOTH languages came
/// from the room's source, so a Vietnamese -> English room rendered its host as "English -> English".
/// </summary>
public class HostParticipantSeedTests
{
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<ITranslationRoomRepository> _mockRoomRepo = new();
    private readonly Mock<ITranslationRoomParticipantRepository> _mockParticipantRepo = new();
    private readonly Mock<ITranslationRoomAudioRouteRepository> _mockAudioRouteRepo = new();
    private readonly Mock<ITranslationRoomSessionRepository> _mockSessionRepo = new();
    private readonly Mock<ITranslationRoomInvitationRepository> _mockInvitationRepo = new();
    private readonly Mock<ILanguagePolicy> _mockLanguagePolicy = new();
    private readonly Mock<IUserSettingsDirectory> _mockUserSettingsDirectory = new();
    private readonly Mock<IWorkspaceMeetingPolicy> _mockWorkspaceMeetingPolicy = new();
    private readonly WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService _service;

    private TranslationRoomParticipant? _seededParticipant;

    public HostParticipantSeedTests()
    {
        _mockUow.Setup(u => u.TranslationRoomRepository).Returns(_mockRoomRepo.Object);
        _mockUow.Setup(u => u.TranslationRoomParticipantRepository).Returns(_mockParticipantRepo.Object);
        _mockUow.Setup(u => u.TranslationRoomAudioRouteRepository).Returns(_mockAudioRouteRepo.Object);
        _mockUow.Setup(u => u.TranslationRoomSessionRepository).Returns(_mockSessionRepo.Object);
        _mockUow.Setup(u => u.TranslationRoomInvitationRepository).Returns(_mockInvitationRepo.Object);

        _mockLanguagePolicy.Setup(p => p.IsSupportedAsync(It.IsAny<string>())).ReturnsAsync(true);
        _mockWorkspaceMeetingPolicy.Setup(p => p.ValidateMeetingCreationAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        // ...and the tenant itself is live unless a test suspends it.
        _mockWorkspaceMeetingPolicy.Setup(p => p.EnsureWorkspaceCanHostMeetingsAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        _mockParticipantRepo
            .Setup(p => p.AddAsync(It.IsAny<TranslationRoomParticipant>(), It.IsAny<CancellationToken>()))
            .Callback<TranslationRoomParticipant, CancellationToken>((p, _) => _seededParticipant = p)
            .Returns(Task.CompletedTask);

        _service = new WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService(
            _mockUow.Object,
            _mockLanguagePolicy.Object,
            new Mock<IAudioRouteEventProcessor>().Object,
            new Mock<ITranslationRoomAudioRouteService>().Object,
            _mockUserSettingsDirectory.Object,
            _mockWorkspaceMeetingPolicy.Object,
            new Mock<WarpTalk.Shared.Interfaces.IEmailService>().Object,
            new Mock<Microsoft.Extensions.Logging.ILogger<
                WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService>>().Object);
    }

    private async Task<TranslationRoomParticipant> CreateRoomAndCaptureHostRowAsync(
        Guid hostId,
        string sourceLanguage,
        List<string> targetLanguages)
    {
        var result = await _service.CreateTranslationRoomAsync(
            new CreateTranslationRoomRequest(
                WorkspaceId: Guid.NewGuid(),
                Title: "Standup",
                Description: null,
                TranslationRoomType: "INSTANT",
                MaxParticipants: null,
                SourceLanguage: sourceLanguage,
                TargetLanguages: targetLanguages,
                Settings: null,
                ScheduledAt: null,
                InvitedEmails: null),
            hostId);

        result.IsSuccess.Should().BeTrue(result.Error);
        _seededParticipant.Should().NotBeNull("WT-82 auto-adds the host as a participant");
        _seededParticipant!.Role.Should().Be("HOST");
        return _seededParticipant!;
    }

    /// <summary>
    /// The host speaks the room's source language and listens in a language the room actually
    /// translates INTO. Seeding both sides from the source produced "English -> English" for a
    /// Vietnamese -> English room, which is not a translation at all.
    /// </summary>
    [Fact]
    public async Task HostRow_DoesNotListenInTheSourceLanguage_WhenSourceAndTargetDiffer()
    {
        var host = await CreateRoomAndCaptureHostRowAsync(
            Guid.NewGuid(), sourceLanguage: "vi", targetLanguages: new List<string> { "en" });

        host.SpeakLanguage.Should().Be("vi");
        host.ListenLanguage.Should().Be("en");
        host.ListenLanguage.Should().NotBe(host.SpeakLanguage);
    }

    /// <summary>
    /// Multi-target rooms: one host row cannot hold three listen languages, so it takes the first
    /// target the creator listed. It must still never fall back to the source.
    /// </summary>
    [Fact]
    public async Task HostRow_ListensInTheFirstTarget_WhenTheRoomHasSeveral()
    {
        var host = await CreateRoomAndCaptureHostRowAsync(
            Guid.NewGuid(), sourceLanguage: "vi", targetLanguages: new List<string> { "en", "ja", "ko" });

        host.SpeakLanguage.Should().Be("vi");
        host.ListenLanguage.Should().Be("en");
    }

    /// <summary>
    /// Production showed a participant literally named "Host". The real name comes from the Auth
    /// directory this service already consults for language defaults.
    /// </summary>
    [Fact]
    public async Task HostRow_IsNotNamedWithTheLiteralPlaceholder()
    {
        var hostId = Guid.NewGuid();
        _mockUserSettingsDirectory
            .Setup(d => d.GetDisplayNameAsync(hostId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Huỳnh Thái Tú");

        var host = await CreateRoomAndCaptureHostRowAsync(
            hostId, sourceLanguage: "vi", targetLanguages: new List<string> { "en" });

        host.DisplayName.Should().NotBeNullOrWhiteSpace();
        host.DisplayName.Should().NotBe("Host");
        host.DisplayName.Should().Be("Huỳnh Thái Tú");
    }

    /// <summary>
    /// A name the directory cannot resolve degrades to the role label. It must never fail room
    /// creation — a cosmetic roster entry is not worth refusing to open a meeting over.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HostRow_FallsBackToTheRoleLabel_WhenTheDirectoryCannotResolveTheName(string? resolved)
    {
        var hostId = Guid.NewGuid();
        _mockUserSettingsDirectory
            .Setup(d => d.GetDisplayNameAsync(hostId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolved);

        var host = await CreateRoomAndCaptureHostRowAsync(
            hostId, sourceLanguage: "vi", targetLanguages: new List<string> { "en" });

        host.DisplayName.Should().Be(TranslationRoomConstants.HostDisplayNameFallback);
    }

    /// <summary>The directory being down is not a reason to refuse to create a room.</summary>
    [Fact]
    public async Task RoomCreation_Succeeds_WhenTheDirectoryThrows()
    {
        var hostId = Guid.NewGuid();
        _mockUserSettingsDirectory
            .Setup(d => d.GetDisplayNameAsync(hostId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("auth unreachable"));

        var host = await CreateRoomAndCaptureHostRowAsync(
            hostId, sourceLanguage: "vi", targetLanguages: new List<string> { "en" });

        host.DisplayName.Should().Be(TranslationRoomConstants.HostDisplayNameFallback);
        host.ListenLanguage.Should().Be("en");
    }
}
