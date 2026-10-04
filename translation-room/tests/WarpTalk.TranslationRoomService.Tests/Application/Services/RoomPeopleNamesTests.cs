using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// WT-422. TranscriptService biases STT toward the names of the people a meeting is with, and this
/// is where those names come from. The host, the people who joined, and the people invited — the
/// last of whom have no roster row until they arrive, so their names come out of Auth by email.
///
/// What is left out matters as much as what is kept: every name here becomes a keyword the
/// recogniser leans toward on marginal audio, so a placeholder label, a kicked guest or a stranger
/// knocking at the lobby must never be one.
/// </summary>
public class RoomPeopleNamesTests
{
    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid HostId = Guid.NewGuid();

    private readonly Mock<ITranslationRoomRepository> _roomRepository = new();
    private readonly Mock<ITranslationRoomParticipantRepository> _participantRepository = new();
    private readonly Mock<ITranslationRoomInvitationRepository> _invitationRepository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IUserSettingsDirectory> _userDirectory = new();

    private readonly List<TranslationRoomParticipant> _roster = new();
    private readonly List<TranslationRoomInvitation> _invitations = new();
    private readonly TranslationRoom _room = new() { Id = RoomId, HostId = HostId, Title = "Sprint review" };

    public RoomPeopleNamesTests()
    {
        _roomRepository
            .Setup(r => r.GetByIdAsync(RoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _room);
        _participantRepository
            .Setup(p => p.FindAsync(
                It.IsAny<Expression<Func<TranslationRoomParticipant, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<TranslationRoomParticipant, bool>> predicate, string _, CancellationToken _) =>
                _roster.Where(predicate.Compile()).ToList());
        _invitationRepository
            .Setup(i => i.FindAsync(
                It.IsAny<Expression<Func<TranslationRoomInvitation, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<TranslationRoomInvitation, bool>> predicate, string _, CancellationToken _) =>
                _invitations.Where(predicate.Compile()).ToList());
        _unitOfWork.Setup(u => u.TranslationRoomInvitationRepository).Returns(_invitationRepository.Object);
    }

    private TranslationRoomDirectoryService Sut(bool withUserDirectory = true) => new(
        _roomRepository.Object,
        _participantRepository.Object,
        _unitOfWork.Object,
        userSettingsDirectory: withUserDirectory ? _userDirectory.Object : null);

    private void Seat(string displayName, string status, Guid? userId = null, int joinedMinute = 0)
        => _roster.Add(new TranslationRoomParticipant
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = RoomId,
            UserId = userId ?? Guid.NewGuid(),
            DisplayName = displayName,
            Role = "PARTICIPANT",
            Status = status,
            JoinedAt = new DateTime(2026, 10, 1, 9, joinedMinute, 0, DateTimeKind.Utc),
            CreatedAt = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc),
        });

    private void Invite(string email, string? accountName, string status = "PENDING", int minute = 0)
    {
        _invitations.Add(new TranslationRoomInvitation
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = RoomId,
            Email = email,
            Status = status,
            CreatedAt = new DateTime(2026, 10, 1, 7, minute, 0, DateTimeKind.Utc),
        });
        _userDirectory
            .Setup(d => d.GetDisplayNameByEmailAsync(email.Trim(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(accountName);
    }

    [Fact]
    public async Task Names_the_host_then_the_room_then_who_left_then_who_is_still_expected()
    {
        // Inserted out of order on purpose: the ranking, not the table order, decides.
        Invite("haruto@example.com", "Tanaka Haruto");
        Seat("Ngô Xuân Hạnh Nhi", TranslationRoomParticipantStatuses.Left, joinedMinute: 1);
        Seat("Trần Mạnh Tuấn", TranslationRoomParticipantStatuses.Disconnected, joinedMinute: 5);
        Seat("Huỳnh Ngọc Kỳ", TranslationRoomParticipantStatuses.Connected, joinedMinute: 3);
        // The host is seeded INVITED and promoted on arrival; they are in the meeting either way.
        Seat("Huỳnh Thái Tú", TranslationRoomParticipantStatuses.Invited, userId: HostId, joinedMinute: 9);

        var result = await Sut().GetPeopleNamesAsync(RoomId, maxNames: 0);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Equal(
            "Huỳnh Thái Tú",
            "Huỳnh Ngọc Kỳ",
            "Trần Mạnh Tuấn",
            "Ngô Xuân Hạnh Nhi",
            "Tanaka Haruto");
    }

    [Fact]
    public async Task A_transferred_host_leads_the_list_not_the_original_booker()
    {
        var newHost = Guid.NewGuid();
        _room.ActiveHostId = newHost;
        Seat("Original Booker", TranslationRoomParticipantStatuses.Connected, userId: HostId, joinedMinute: 1);
        Seat("Current Host", TranslationRoomParticipantStatuses.Connected, userId: newHost, joinedMinute: 2);

        var result = await Sut().GetPeopleNamesAsync(RoomId, maxNames: 0);

        result.Value.Should().Equal("Current Host", "Original Booker");
    }

    [Fact]
    public async Task Leaves_out_everyone_the_meeting_is_not_with_and_every_placeholder_label()
    {
        Seat("Huỳnh Thái Tú", TranslationRoomParticipantStatuses.Connected, userId: HostId);
        Seat("Kicked Guest", TranslationRoomParticipantStatuses.Kicked);
        Seat("Rejected Guest", TranslationRoomParticipantStatuses.Rejected);
        // On the roster, WAITING and non-host INVITED both mean "knocked, never admitted" (WT-563).
        Seat("Stranger At The Door", TranslationRoomParticipantStatuses.Waiting);
        Seat("Left The Lobby", TranslationRoomParticipantStatuses.Invited);
        // The bridge's far-side stand-in is a label, not a person.
        _roster.Add(new TranslationRoomParticipant
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = RoomId,
            UserId = TranslationRoomConstants.ExternalBridgeParticipantUserId,
            DisplayName = TranslationRoomConstants.ExternalBridgeDisplayName,
            Role = "PARTICIPANT",
            Status = TranslationRoomParticipantStatuses.Connected,
            CreatedAt = DateTime.UtcNow,
        });
        // A host whose name could not be resolved was seeded with the role label instead.
        Seat(TranslationRoomConstants.HostDisplayNameFallback, TranslationRoomParticipantStatuses.Connected);

        var result = await Sut().GetPeopleNamesAsync(RoomId, maxNames: 0);

        result.Value.Should().Equal("Huỳnh Thái Tú");
    }

    [Fact]
    public async Task An_invitee_is_named_once_and_declined_or_accountless_invitations_name_nobody()
    {
        Seat("Huỳnh Thái Tú", TranslationRoomParticipantStatuses.Connected, userId: HostId);
        Seat("Huỳnh Ngọc Kỳ", TranslationRoomParticipantStatuses.Connected, joinedMinute: 2);
        // Invited AND already joined: the same person, named once.
        Invite("ky@example.com", "Huỳnh  Ngọc Kỳ", minute: 1);
        Invite("declined@example.com", "Declined Person", status: "DECLINED", minute: 2);
        Invite("no-account@example.com", accountName: null, minute: 3);
        Invite(" Yamada@Example.com ", "Yamada Sakura", status: "ACCEPTED", minute: 4);
        // The same address twice is one lookup.
        Invite("yamada@example.com", "Yamada Sakura", minute: 5);

        var result = await Sut().GetPeopleNamesAsync(RoomId, maxNames: 0);

        result.Value.Should().Equal("Huỳnh Thái Tú", "Huỳnh Ngọc Kỳ", "Yamada Sakura");
        _userDirectory.Verify(
            d => d.GetDisplayNameByEmailAsync("declined@example.com", It.IsAny<CancellationToken>()),
            Times.Never);
        _userDirectory.Verify(
            d => d.GetDisplayNameByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task Stops_at_the_requested_count_and_does_not_ask_Auth_for_more_than_it_can_use()
    {
        Seat("Huỳnh Thái Tú", TranslationRoomParticipantStatuses.Connected, userId: HostId);
        Seat("Huỳnh Ngọc Kỳ", TranslationRoomParticipantStatuses.Connected, joinedMinute: 1);
        for (var i = 0; i < 10; i++)
            Invite($"invitee{i}@example.com", $"Invitee Number{i}", minute: i);

        var result = await Sut().GetPeopleNamesAsync(RoomId, maxNames: 3);

        result.Value.Should().Equal("Huỳnh Thái Tú", "Huỳnh Ngọc Kỳ", "Invitee Number0");
        _userDirectory.Verify(
            d => d.GetDisplayNameByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Without_Auth_or_with_Auth_failing_the_roster_names_still_come_back()
    {
        Seat("Huỳnh Thái Tú", TranslationRoomParticipantStatuses.Connected, userId: HostId);
        Invite("haruto@example.com", "Tanaka Haruto");

        var withoutDirectory = await Sut(withUserDirectory: false).GetPeopleNamesAsync(RoomId, maxNames: 0);
        withoutDirectory.Value.Should().Equal("Huỳnh Thái Tú");

        _userDirectory
            .Setup(d => d.GetDisplayNameByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Auth is down"));
        var withFailingDirectory = await Sut().GetPeopleNamesAsync(RoomId, maxNames: 0);
        withFailingDirectory.IsSuccess.Should().BeTrue();
        withFailingDirectory.Value.Should().Equal("Huỳnh Thái Tú");
    }

    [Fact]
    public async Task A_missing_room_is_not_found()
    {
        var result = await Sut().GetPeopleNamesAsync(Guid.NewGuid(), maxNames: 0);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }
}
