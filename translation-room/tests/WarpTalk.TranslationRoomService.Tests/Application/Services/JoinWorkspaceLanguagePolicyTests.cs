using FluentAssertions;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.LanguagePolicy;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// WT-707: joining a room checks the workspace's language whitelist (L1).
///
/// The REST join only ever asked whether the PLATFORM supports a language, so a participant row
/// stored `ja` in a workspace whose owner permits vi/en only. The pre-join picker narrows what is
/// offered, but a client that skips the picker skipped the rule with it — enforcement has to live
/// on the server path that writes the participant.
///
/// The rule these pin: a whitelist that was READ and excludes the language refuses the join
/// before anything is written; a whitelist that could not be read (or is empty, which means
/// "unrestricted") lets the join through, the same fail-open the suspension check makes, because
/// a WorkspaceService blip must not lock everyone out of a live meeting.
/// </summary>
public class JoinWorkspaceLanguagePolicyTests
{
    private const string RoomCode = "abc-defg-hij";

    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<ITranslationRoomRepository> _mockRoomRepo = new();
    private readonly Mock<ITranslationRoomParticipantRepository> _mockParticipantRepo = new();
    private readonly Mock<ITranslationRoomAudioRouteRepository> _mockAudioRouteRepo = new();
    private readonly Mock<ILanguagePolicy> _mockLanguagePolicy = new();
    private readonly Mock<ITranslationRoomAudioRouteService> _mockAudioRouteService = new();
    private readonly Mock<IWorkspaceMeetingPolicy> _mockWorkspaceMeetingPolicy = new();
    private readonly Mock<IWorkspaceMemberDirectory> _mockWorkspaceMemberDirectory = new();

    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService _service;

    public JoinWorkspaceLanguagePolicyTests()
    {
        _mockUow.Setup(u => u.TranslationRoomRepository).Returns(_mockRoomRepo.Object);
        _mockUow.Setup(u => u.TranslationRoomParticipantRepository).Returns(_mockParticipantRepo.Object);
        _mockUow.Setup(u => u.TranslationRoomAudioRouteRepository).Returns(_mockAudioRouteRepo.Object);

        _mockParticipantRepo.Setup(p => p.GetByRoomIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomParticipant>());
        _mockAudioRouteService.Setup(s => s.GenerateRoutesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new List<TranslationRoomAudioRouteDto>()));

        // The platform supports everything; only the workspace's rule is under test.
        _mockLanguagePolicy.Setup(v => v.IsSupportedAsync(It.IsAny<string>())).ReturnsAsync(true);
        _mockLanguagePolicy.Setup(v => v.ValidateParticipantLanguagesAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TranslationRoom>()))
            .ReturnsAsync((string?)null);

        _mockWorkspaceMeetingPolicy.Setup(p => p.EnsureWorkspaceCanHostMeetingsAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        _mockWorkspaceMemberDirectory.Setup(d => d.IsMemberAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _service = new WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService(
            _mockUow.Object,
            _mockLanguagePolicy.Object,
            new Mock<IAudioRouteEventProcessor>().Object,
            _mockAudioRouteService.Object,
            new Mock<IUserSettingsDirectory>().Object,
            _mockWorkspaceMeetingPolicy.Object,
            _mockWorkspaceMemberDirectory.Object,
            new Mock<WarpTalk.Shared.Interfaces.IEmailService>().Object,
            new Mock<Microsoft.Extensions.Logging.ILogger<
                WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService>>().Object,
            redisStateRepository: new Mock<IRedisStateRepository>().Object);
    }

    private TranslationRoom ArrangeRoom(Guid? workspaceId = null)
    {
        var room = new TranslationRoom
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId ?? _workspaceId,
            HostId = Guid.NewGuid(),
            TranslationRoomCode = RoomCode,
            Status = "WAITING",
            TranslationRoomType = "INSTANT",
            SourceLanguage = "vi",
            TargetLanguages = "[\"en\"]",
            Settings = "{\"requires_approval\":false}"
        };

        _mockRoomRepo.Setup(r => r.GetByCodeAsync(RoomCode, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        _mockRoomRepo.Setup(r => r.GetByIdAsync(room.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        _mockParticipantRepo.Setup(p => p.GetByRoomAndUserAsync(room.Id, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomParticipant?)null);

        return room;
    }

    private void ArrangeWorkspaceAllows(params string[] languages) =>
        _mockWorkspaceMeetingPolicy
            .Setup(p => p.GetAllowedLanguagesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<string>>(languages));

    private Task<Result<JoinTranslationRoomResponse>> JoinAs(string speak, string listen) =>
        _service.JoinTranslationRoomAsync(
            new JoinTranslationRoomRequest(RoomCode, "User", speak, listen), Guid.NewGuid());

    private void VerifyNoParticipantWritten()
    {
        _mockParticipantRepo.Verify(
            p => p.AddAsync(It.IsAny<TranslationRoomParticipant>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockParticipantRepo.Verify(p => p.Update(It.IsAny<TranslationRoomParticipant>()), Times.Never);
        _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Join_IsRefused_WhenTheWorkspaceExcludesTheSpeakLanguage()
    {
        // The reported case: vi/en workspace, participant speaks Japanese.
        ArrangeRoom();
        ArrangeWorkspaceAllows("vi", "en");

        var result = await JoinAs(speak: "ja", listen: "vi");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        result.Error.Should().Be("This workspace does not allow 'ja' in meetings.");
        VerifyNoParticipantWritten();
    }

    [Fact]
    public async Task Join_IsRefused_WhenTheWorkspaceExcludesTheListenLanguage()
    {
        ArrangeRoom();
        ArrangeWorkspaceAllows("vi", "en");

        var result = await JoinAs(speak: "vi", listen: "ja-JP");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        result.Error.Should().Contain("'ja'");
        VerifyNoParticipantWritten();
    }

    [Fact]
    public async Task Join_IsAllowed_WhenBothLanguagesAreWhitelisted_InAnyRegionalForm()
    {
        // The whitelist and the request are compared normalized: "vi-VN" is "vi".
        ArrangeRoom();
        ArrangeWorkspaceAllows("vi-VN", "EN");

        var result = await JoinAs(speak: "vi-VN", listen: "en");

        result.IsSuccess.Should().BeTrue();
        _mockParticipantRepo.Verify(
            p => p.AddAsync(It.IsAny<TranslationRoomParticipant>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Join_FailsOpen_WhenTheWhitelistLookupFails()
    {
        ArrangeRoom();
        _mockWorkspaceMeetingPolicy
            .Setup(p => p.GetAllowedLanguagesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<IReadOnlyList<string>>(
                "Workspace service is unavailable.", ErrorCodes.ServiceUnavailable));

        var result = await JoinAs(speak: "ja", listen: "vi");

        result.IsSuccess.Should().BeTrue();
        _mockParticipantRepo.Verify(
            p => p.AddAsync(It.IsAny<TranslationRoomParticipant>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Join_FailsOpen_WhenTheWhitelistLookupThrows()
    {
        ArrangeRoom();
        _mockWorkspaceMeetingPolicy
            .Setup(p => p.GetAllowedLanguagesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("channel closed"));

        var result = await JoinAs(speak: "ja", listen: "vi");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Join_FailsOpen_WhenTheLookupAnswersNull()
    {
        // A loose mock (and any client that swallows its own error) returns a null Result. That
        // must read as "unknown", not throw inside join.
        ArrangeRoom();

        var result = await JoinAs(speak: "ja", listen: "vi");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Join_IsAllowed_WhenTheWhitelistIsEmpty()
    {
        // Empty means "no restriction", not "nothing allowed".
        ArrangeRoom();
        ArrangeWorkspaceAllows();

        var result = await JoinAs(speak: "ja", listen: "ko");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Join_DoesNotAskForAWhitelist_WhenTheRoomBelongsToNoWorkspace()
    {
        // An external-bridge room carries Guid.Empty: there is no owner, so no rule to ask for.
        ArrangeRoom(workspaceId: Guid.Empty);

        var result = await JoinAs(speak: "ja", listen: "vi");

        result.IsSuccess.Should().BeTrue();
        _mockWorkspaceMeetingPolicy.Verify(
            p => p.GetAllowedLanguagesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JoinById_IsRefused_ByTheSameRule()
    {
        // Join-by-id (shared meeting links) delegates to the by-code join; this pins that the
        // delegation keeps the check rather than routing around it.
        var room = ArrangeRoom();
        ArrangeWorkspaceAllows("vi", "en");

        var result = await _service.JoinTranslationRoomByIdAsync(
            room.Id, new JoinTranslationRoomByIdRequest("User", "ja", "vi"), Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        result.Error.Should().Contain("'ja'");
        VerifyNoParticipantWritten();
    }
}
