using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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
/// WT-552: inviting somebody once the meeting is already running.
///
/// The only path that could add an invitee was UpdateTranslationRoomSettingsAsync, and it refuses
/// the moment a room leaves SCHEDULED — "Room settings cannot be updated after the room has
/// entered IN_PROGRESS status" — which is exactly when a host realises they need one more person.
/// There was no way to do it at all, which is what the ticket reports.
///
/// That freeze is NOT relaxed. Languages and approval policy must not change under people already
/// in the room; inviting changes neither, so it gets its own door rather than a hole in that guard.
/// </summary>
public class InviteDuringMeetingTests
{
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<ITranslationRoomRepository> _mockRoomRepo = new();
    private readonly Mock<ITranslationRoomParticipantRepository> _mockParticipantRepo = new();
    private readonly Mock<ITranslationRoomInvitationRepository> _mockInvitationRepo = new();
    private readonly Mock<ILanguagePolicy> _mockLanguagePolicy = new();
    private readonly Mock<IAudioRouteEventProcessor> _mockAudioRouteEventProcessor = new();
    private readonly Mock<ITranslationRoomAudioRouteService> _mockAudioRouteService = new();
    private readonly Mock<IUserSettingsDirectory> _mockUserSettingsDirectory = new();
    private readonly Mock<IWorkspaceMeetingPolicy> _mockWorkspaceMeetingPolicy = new();
    private readonly Mock<IWorkspaceMemberDirectory> _mockWorkspaceMemberDirectory = new();
    private readonly Mock<WarpTalk.Shared.Interfaces.IEmailService> _mockEmailService = new();
    private readonly Mock<IRedisStateRepository> _mockRedis = new();
    private readonly WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService _service;

    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid HostId = Guid.NewGuid();

    public InviteDuringMeetingTests()
    {
        _mockUow.Setup(u => u.TranslationRoomRepository).Returns(_mockRoomRepo.Object);
        _mockUow.Setup(u => u.TranslationRoomParticipantRepository).Returns(_mockParticipantRepo.Object);
        _mockUow.Setup(u => u.TranslationRoomInvitationRepository).Returns(_mockInvitationRepo.Object);

        _service = new WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService(
            _mockUow.Object,
            _mockLanguagePolicy.Object,
            _mockAudioRouteEventProcessor.Object,
            _mockAudioRouteService.Object,
            _mockUserSettingsDirectory.Object,
            _mockWorkspaceMeetingPolicy.Object,
            _mockWorkspaceMemberDirectory.Object,
            _mockEmailService.Object,
            new Mock<ILogger<WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService>>().Object,
            redisStateRepository: _mockRedis.Object);
    }

    private static TranslationRoom Room(string status) => new()
    {
        Id = RoomId,
        HostId = HostId,
        WorkspaceId = Guid.NewGuid(),
        Title = "Standup",
        Status = status,
        TranslationRoomCode = "abc-def-ghi",
    };

    private void RoomIs(TranslationRoom room, params TranslationRoomInvitation[] alreadyInvited)
    {
        _mockRoomRepo
            .Setup(r => r.GetByIdAsync(RoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        _mockInvitationRepo
            .Setup(r => r.FindAsync(
                It.IsAny<Expression<Func<TranslationRoomInvitation, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(alreadyInvited.ToList());
    }

    [Fact]
    public async Task AHostCanInviteWhileTheMeetingIsRunning()
    {
        // The whole point of the ticket: IN_PROGRESS is the state the old path refused.
        RoomIs(Room("IN_PROGRESS"));

        var result = await _service.InviteParticipantsAsync(
            RoomId, HostId, new[] { "someone@acme.com" });

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(1);
        _mockInvitationRepo.Verify(r => r.AddAsync(
            It.Is<TranslationRoomInvitation>(i => i.Email == "someone@acme.com" && i.Status == "PENDING"),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TheInviteeIsEmailedALinkThatSurvivesTheForward()
    {
        RoomIs(Room("IN_PROGRESS"));

        await _service.InviteParticipantsAsync(RoomId, HostId, new[] { "someone@acme.com" });

        // Id, not room code — WT-528: /room/{code} does not survive the forward.
        _mockEmailService.Verify(e => e.SendMeetingInvitationAsync(
            "someone@acme.com",
            It.IsAny<string>(),
            It.Is<string>(link => link.EndsWith($"/room/{RoomId}")),
            "Standup",
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TheInviteesRoomListIsToldOnlyAfterSomebodyWasReallyAdded()
    {
        // WT-187: without this publish, the invitee sees nothing until they reload.
        RoomIs(Room("IN_PROGRESS"));

        await _service.InviteParticipantsAsync(RoomId, HostId, new[] { "someone@acme.com" });

        _mockRedis.Verify(
            r => r.PublishAsync(It.IsAny<string>(), It.Is<string>(p => p.Contains("MeetingInvited") && p.Contains(RoomId.ToString()))),
            Times.Once);
    }

    [Fact]
    public async Task OnlyTheHostMayInvite()
    {
        RoomIs(Room("IN_PROGRESS"));

        var result = await _service.InviteParticipantsAsync(
            RoomId, Guid.NewGuid(), new[] { "someone@acme.com" });

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Unauthorized);
        _mockInvitationRepo.Verify(r => r.AddAsync(
            It.IsAny<TranslationRoomInvitation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AFinishedMeetingCannotBeInvitedTo()
    {
        // The link would go nowhere and the invitee would be sent to a room they cannot enter.
        RoomIs(Room(TranslationRoomConstants.TerminalStatuses[0]));

        var result = await _service.InviteParticipantsAsync(
            RoomId, HostId, new[] { "someone@acme.com" });

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidState);
        _mockInvitationRepo.Verify(r => r.AddAsync(
            It.IsAny<TranslationRoomInvitation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReInvitingSomebodyIsANoOpRatherThanASecondEmail()
    {
        // A host adding one person to a group of five should not have to remember which of them
        // were already invited, and a second email reads as the meeting starting again.
        // Cased differently on purpose: addresses are matched case-insensitively.
        RoomIs(
            Room("IN_PROGRESS"),
            new TranslationRoomInvitation
            {
                TranslationRoomId = RoomId,
                Email = "Someone@ACME.com",
                Status = "PENDING",
            });

        var result = await _service.InviteParticipantsAsync(
            RoomId, HostId, new[] { "someone@acme.com" });

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(0);
        _mockEmailService.Verify(e => e.SendMeetingInvitationAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _mockRedis.Verify(
            r => r.PublishAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task TheSameAddressTwiceInOneRequestIsInvitedOnce()
    {
        // The dedupe has to cover the request itself, not just what is already stored — the UI
        // lets a host paste a list, and a repeat inside one paste is the common way to get two.
        RoomIs(Room("IN_PROGRESS"));

        var result = await _service.InviteParticipantsAsync(
            RoomId, HostId, new[] { "someone@acme.com", " someone@acme.com ", "  " });

        result.Value.Should().Be(1);
        _mockInvitationRepo.Verify(r => r.AddAsync(
            It.IsAny<TranslationRoomInvitation>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AMissingRoomIsNotFound()
    {
        _mockRoomRepo
            .Setup(r => r.GetByIdAsync(RoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoom?)null);

        var result = await _service.InviteParticipantsAsync(
            RoomId, HostId, new[] { "someone@acme.com" });

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }
}
