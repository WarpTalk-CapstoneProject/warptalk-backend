using System;
using System.Collections.Generic;
using System.Linq;
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
/// What the far side of a new EXTERNAL_BRIDGE room speaks, end to end through
/// CreateTranslationRoomAsync.
///
/// THE BUG
///   The stand-in was seeded from <c>targetLanguages[0]</c>, and both clients that create bridge
///   rooms — the create dialog and the desktop's automatic Google Meet room — put the host's own
///   language first. Every bridge room was therefore born with host and far side on one language,
///   which the audio mesh answers with no route at all: nothing translated, nothing dubbed, and no
///   error anywhere.
/// </summary>
public class ExternalBridgeCreateLanguageTests
{
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<ITranslationRoomRepository> _mockRoomRepo = new();
    private readonly Mock<ITranslationRoomParticipantRepository> _mockParticipantRepo = new();
    private readonly Mock<ILanguagePolicy> _mockLanguagePolicy = new();
    private readonly Mock<IWorkspaceMeetingPolicy> _mockWorkspaceMeetingPolicy = new();
    private readonly WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService _service;

    private readonly List<TranslationRoomParticipant> _seeded = new();
    private IEnumerable<string>? _policyVettedTargets;

    public ExternalBridgeCreateLanguageTests()
    {
        _mockUow.Setup(u => u.TranslationRoomRepository).Returns(_mockRoomRepo.Object);
        _mockUow.Setup(u => u.TranslationRoomParticipantRepository).Returns(_mockParticipantRepo.Object);
        _mockUow.Setup(u => u.TranslationRoomAudioRouteRepository).Returns(new Mock<ITranslationRoomAudioRouteRepository>().Object);
        _mockUow.Setup(u => u.TranslationRoomSessionRepository).Returns(new Mock<ITranslationRoomSessionRepository>().Object);
        _mockUow.Setup(u => u.TranslationRoomInvitationRepository).Returns(new Mock<ITranslationRoomInvitationRepository>().Object);

        _mockLanguagePolicy.Setup(p => p.IsSupportedAsync(It.IsAny<string>())).ReturnsAsync(true);
        _mockWorkspaceMeetingPolicy.Setup(p => p.ValidateMeetingCreationAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, IEnumerable<string>, string?, CancellationToken>((_, _, targets, _, _) => _policyVettedTargets = targets.ToList())
            .ReturnsAsync(Result.Success());
        _mockWorkspaceMeetingPolicy.Setup(p => p.EnsureWorkspaceCanHostMeetingsAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        _mockParticipantRepo
            .Setup(p => p.AddAsync(It.IsAny<TranslationRoomParticipant>(), It.IsAny<CancellationToken>()))
            .Callback<TranslationRoomParticipant, CancellationToken>((p, _) => _seeded.Add(p))
            .Returns(Task.CompletedTask);

        _service = new WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService(
            _mockUow.Object,
            _mockLanguagePolicy.Object,
            new Mock<IAudioRouteEventProcessor>().Object,
            new Mock<ITranslationRoomAudioRouteService>().Object,
            new Mock<IUserSettingsDirectory>().Object,
            _mockWorkspaceMeetingPolicy.Object,
            new Mock<IWorkspaceMemberDirectory>().Object,
            new Mock<WarpTalk.Shared.Interfaces.IEmailService>().Object,
            new Mock<Microsoft.Extensions.Logging.ILogger<
                WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService>>().Object);
    }

    private async Task<(TranslationRoomParticipant Host, TranslationRoomParticipant FarSide)> CreateBridgeAsync(
        string source,
        List<string> targets,
        string? externalMeetingLanguage = null)
    {
        var result = await _service.CreateTranslationRoomAsync(
            new CreateTranslationRoomRequest(
                WorkspaceId: Guid.NewGuid(),
                Title: "Google Meet call",
                Description: null,
                TranslationRoomType: TranslationRoomTypes.ExternalBridge,
                MaxParticipants: null,
                SourceLanguage: source,
                TargetLanguages: targets,
                Settings: null,
                ScheduledAt: null,
                InvitedEmails: null,
                ExternalMeetingLanguage: externalMeetingLanguage),
            Guid.NewGuid());

        result.IsSuccess.Should().BeTrue(result.Error);
        var host = _seeded.Single(p => p.Role == "HOST");
        var farSide = _seeded.Single(p => p.UserId == TranslationRoomConstants.ExternalBridgeParticipantUserId);
        return (host, farSide);
    }

    [Fact]
    public async Task TheHostsOwnLanguageListedFirst_NoLongerBecomesTheFarSidesLanguage()
    {
        // Exactly what both web clients sent: the source is one of the targets, and first.
        var (host, farSide) = await CreateBridgeAsync("vi", new List<string> { "vi", "en" });

        host.SpeakLanguage.Should().Be("vi");
        farSide.SpeakLanguage.Should().Be("en");
        farSide.ListenLanguage.Should().Be("en");
        farSide.SpeakLanguage.Should().NotBe(host.ListenLanguage, "a same-language pair has no route");
    }

    [Fact]
    public async Task AnExplicitFarSideLanguage_WinsOverThePositionOfTheTargets()
    {
        var (_, farSide) = await CreateBridgeAsync("vi", new List<string> { "vi", "en", "ja" }, externalMeetingLanguage: "ja-JP");

        farSide.SpeakLanguage.Should().Be("ja");
        farSide.ListenLanguage.Should().Be("ja");
    }

    [Fact]
    public async Task AnExplicitFarSideLanguage_BecomesARoomLanguage_AndIsVettedByTheWorkspacePolicy()
    {
        await CreateBridgeAsync("vi", new List<string> { "vi" }, externalMeetingLanguage: "ko");

        _policyVettedTargets.Should().NotBeNull();
        _policyVettedTargets!.Should().Contain("ko",
            "a language the room uses must pass the same whitelist as the ones it was created with");
    }

    [Fact]
    public async Task AnUnsupportedFarSideLanguage_IsRefused()
    {
        _mockLanguagePolicy.Setup(p => p.IsSupportedAsync("xx")).ReturnsAsync(false);

        var result = await _service.CreateTranslationRoomAsync(
            new CreateTranslationRoomRequest(
                Guid.NewGuid(), "Google Meet call", null, TranslationRoomTypes.ExternalBridge, null,
                "vi", new List<string> { "vi" }, null, null, null,
                ExternalMeetingLanguage: "xx"),
            Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        _seeded.Should().BeEmpty();
    }

    [Fact]
    public async Task AOneLanguageRoom_IsStillCreated()
    {
        // A workspace that allows one language cannot give the far side another. The room is made
        // anyway; the client is what says translation needs a second language.
        var (host, farSide) = await CreateBridgeAsync("vi", new List<string> { "vi" });

        farSide.SpeakLanguage.Should().Be(host.SpeakLanguage);
    }
}
