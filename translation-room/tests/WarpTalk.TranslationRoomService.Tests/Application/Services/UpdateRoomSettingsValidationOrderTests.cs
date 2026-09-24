using FluentAssertions;
using Grpc.Core;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.LanguagePolicy;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// WT-707: a refused room edit has no side effects.
///
/// UpdateTranslationRoomSettingsAsync used to assign the title and add invitations — emailing and
/// notifying each invitee — BEFORE it checked the requested languages against the platform and
/// the workspace. An edit refused for a forbidden language therefore still told people about a
/// meeting change that was never saved, and an email cannot be rolled back with the transaction.
/// Every check now runs first; these pin that nothing leaves the service when one of them says no,
/// and pair it with the same edit succeeding so a method that refused everything would not pass.
/// </summary>
public class UpdateRoomSettingsValidationOrderTests
{
    private const string Invitee = "invitee@warptalk.io.vn";

    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<ITranslationRoomRepository> _mockRoomRepo = new();
    private readonly Mock<ITranslationRoomInvitationRepository> _mockInvitationRepo = new();
    private readonly Mock<ILanguagePolicy> _mockLanguagePolicy = new();
    private readonly Mock<IWorkspaceMeetingPolicy> _mockWorkspaceMeetingPolicy = new();
    private readonly Mock<WarpTalk.Shared.Interfaces.IEmailService> _mockEmailService = new();
    private readonly Mock<WarpTalk.Shared.Protos.UserService.UserServiceClient> _mockUserClient = new();
    private readonly Mock<WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient> _mockNotificationClient = new();

    private readonly Guid _hostId = Guid.NewGuid();
    private readonly TranslationRoom _room;
    private readonly WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService _service;

    public UpdateRoomSettingsValidationOrderTests()
    {
        _room = new TranslationRoom
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            HostId = _hostId,
            Status = "WAITING",
            Title = "Standup",
            TranslationRoomCode = "abc-defg-hij",
            SourceLanguage = "vi",
            TargetLanguages = "[\"en\"]",
            Settings = "{\"requires_approval\":true}"
        };

        _mockUow.Setup(u => u.TranslationRoomRepository).Returns(_mockRoomRepo.Object);
        _mockUow.Setup(u => u.TranslationRoomInvitationRepository).Returns(_mockInvitationRepo.Object);
        _mockRoomRepo.Setup(r => r.GetByIdAsync(_room.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_room);
        _mockInvitationRepo.Setup(r => r.FindAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<TranslationRoomInvitation, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TranslationRoomInvitation>());

        _mockLanguagePolicy.Setup(v => v.IsSupportedAsync(It.IsAny<string>())).ReturnsAsync(true);
        _mockWorkspaceMeetingPolicy.Setup(p => p.ValidateRoomLanguagesAsync(
                It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        _service = new WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService(
            _mockUow.Object,
            _mockLanguagePolicy.Object,
            new Mock<IAudioRouteEventProcessor>().Object,
            new Mock<ITranslationRoomAudioRouteService>().Object,
            new Mock<IUserSettingsDirectory>().Object,
            _mockWorkspaceMeetingPolicy.Object,
            new Mock<IWorkspaceMemberDirectory>().Object,
            _mockEmailService.Object,
            new Mock<Microsoft.Extensions.Logging.ILogger<
                WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService>>().Object,
            redisStateRepository: new Mock<IRedisStateRepository>().Object,
            // Real (mocked) clients so the in-app notification path is live and observable: with
            // either left null, NotifyInvitedUserAsync returns early and "never notified" would
            // hold for the wrong reason.
            notificationClient: _mockNotificationClient.Object,
            userClient: _mockUserClient.Object);
    }

    private static UpdateRoomSettingsRequest EditWithInviteAndLanguages(List<string> targets) =>
        new(
            Title: "Renamed standup",
            Description: null,
            MaxParticipants: null,
            ScheduledAt: null,
            InvitedEmails: new List<string> { Invitee },
            Settings: null,
            SourceLanguage: "vi",
            TargetLanguages: targets);

    private void VerifyNothingLeftTheService()
    {
        _mockEmailService.Verify(
            e => e.SendMeetingInvitationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockUserClient.Verify(
            u => u.GetUserByEmailAsync(
                It.IsAny<WarpTalk.Shared.Protos.GetUserByEmailRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockInvitationRepo.Verify(
            r => r.AddAsync(It.IsAny<TranslationRoomInvitation>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefusedByTheWorkspace_SendsNoInvitation_AndChangesNothing()
    {
        _mockWorkspaceMeetingPolicy.Setup(p => p.ValidateRoomLanguagesAsync(
                It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(
                "This workspace does not allow 'es' in meetings.", ErrorCodes.ValidationError));

        var result = await _service.UpdateTranslationRoomSettingsAsync(
            _room.Id, _hostId, EditWithInviteAndLanguages(new List<string> { "es" }));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        _room.Title.Should().Be("Standup");
        _room.TargetLanguages.Should().Be("[\"en\"]");
        VerifyNothingLeftTheService();
    }

    [Fact]
    public async Task RefusedByThePlatform_SendsNoInvitation_AndChangesNothing()
    {
        _mockLanguagePolicy.Setup(v => v.IsSupportedAsync("xx")).ReturnsAsync(false);

        var result = await _service.UpdateTranslationRoomSettingsAsync(
            _room.Id, _hostId, EditWithInviteAndLanguages(new List<string> { "xx" }));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        _room.Title.Should().Be("Standup");
        VerifyNothingLeftTheService();
    }

    [Fact]
    public async Task AcceptedEdit_StillInvites_AndSaves()
    {
        // The counterpart: the reorder moved the side effects, it did not remove them.
        var result = await _service.UpdateTranslationRoomSettingsAsync(
            _room.Id, _hostId, EditWithInviteAndLanguages(new List<string> { "en" }));

        result.IsSuccess.Should().BeTrue();
        _room.Title.Should().Be("Renamed standup");
        _mockInvitationRepo.Verify(
            r => r.AddAsync(It.Is<TranslationRoomInvitation>(i => i.Email == Invitee), It.IsAny<CancellationToken>()),
            Times.Once);
        // The email quotes the EDITED title — the field assignment still precedes the invitations.
        _mockEmailService.Verify(
            e => e.SendMeetingInvitationAsync(
                Invitee, It.IsAny<string>(), It.IsAny<string>(),
                "Renamed standup", It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DuplicateTargets_AreStoredOnce_AndValidatedOnce()
    {
        var result = await _service.UpdateTranslationRoomSettingsAsync(
            _room.Id, _hostId, EditWithInviteAndLanguages(new List<string> { "en", "en-US", "EN", "ja" }));

        result.IsSuccess.Should().BeTrue();
        LanguageHelper.ParseTargetLanguages(_room.TargetLanguages).Should().Equal("en", "ja");
        _mockWorkspaceMeetingPolicy.Verify(p => p.ValidateRoomLanguagesAsync(
                _room.WorkspaceId,
                "vi",
                It.Is<IEnumerable<string>>(targets => targets.SequenceEqual(new[] { "en", "ja" })),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
