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
/// WT-262. TranslationRoom.MaxParticipants is stamped at creation from TranslationRoomTypePolicy
/// but had zero readers, so a VIRTUAL_APPOINTMENT capped at 2 accepted an unbounded roster. These
/// pin the cap and, just as importantly, the three carve-outs that keep it from locking people out.
/// </summary>
public class TranslationRoomCapacityTests
{
    private const string RoomCode = "abc-defg-hij";

    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<ITranslationRoomRepository> _mockRoomRepo = new();
    private readonly Mock<ITranslationRoomParticipantRepository> _mockParticipantRepo = new();
    private readonly Mock<ITranslationRoomAudioRouteRepository> _mockAudioRouteRepo = new();
    private readonly Mock<ITranslationRoomSessionRepository> _mockSessionRepo = new();
    private readonly Mock<ILanguagePolicy> _mockLanguagePolicy = new();
    private readonly Mock<IAudioRouteEventProcessor> _mockAudioRouteEventProcessor = new();
    private readonly Mock<ITranslationRoomAudioRouteService> _mockAudioRouteService = new();
    private readonly Mock<IUserSettingsDirectory> _mockUserSettingsDirectory = new();
    private readonly Mock<IWorkspaceMeetingPolicy> _mockWorkspaceMeetingPolicy = new();
    private readonly Mock<WarpTalk.Shared.Interfaces.IEmailService> _mockEmailService = new();
    private readonly Mock<IRedisStateRepository> _mockRedisStateRepository = new();
    private readonly Mock<Microsoft.Extensions.Logging.ILogger<WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService>> _mockLogger = new();
    private readonly WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService _service;

    public TranslationRoomCapacityTests()
    {
        _mockUow.Setup(u => u.TranslationRoomRepository).Returns(_mockRoomRepo.Object);
        _mockUow.Setup(u => u.TranslationRoomParticipantRepository).Returns(_mockParticipantRepo.Object);
        _mockUow.Setup(u => u.TranslationRoomAudioRouteRepository).Returns(_mockAudioRouteRepo.Object);
        _mockUow.Setup(u => u.TranslationRoomSessionRepository).Returns(_mockSessionRepo.Object);

        _mockParticipantRepo.Setup(p => p.GetByRoomIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomParticipant>());
        _mockLanguagePolicy.Setup(v => v.IsSupportedAsync(It.IsAny<string>())).ReturnsAsync(true);
        _mockLanguagePolicy.Setup(v => v.ValidateParticipantLanguagesAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TranslationRoom>()))
            .ReturnsAsync((string?)null);
        _mockWorkspaceMeetingPolicy.Setup(p => p.ValidateMeetingCreationAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        _service = new WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService(
            _mockUow.Object,
            _mockLanguagePolicy.Object,
            _mockAudioRouteEventProcessor.Object,
            _mockAudioRouteService.Object,
            _mockUserSettingsDirectory.Object,
            _mockWorkspaceMeetingPolicy.Object,
            _mockEmailService.Object,
            _mockLogger.Object,
            redisStateRepository: _mockRedisStateRepository.Object);
    }

    private TranslationRoom ArrangeRoom(Guid hostId, int maxParticipants)
    {
        var room = new TranslationRoom
        {
            Id = Guid.NewGuid(),
            HostId = hostId,
            TranslationRoomCode = RoomCode,
            Status = "WAITING",
            TranslationRoomType = "INSTANT",
            MaxParticipants = maxParticipants,
            Settings = "{\"requires_approval\":false,\"history_access\":\"HostOnly\"}"
        };

        _mockRoomRepo.Setup(r => r.GetByCodeAsync(RoomCode, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        return room;
    }

    private void ArrangeSeatsTaken(TranslationRoom room, int seats) =>
        _mockParticipantRepo
            .Setup(p => p.CountSeatHoldingParticipantsAsync(room.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(seats);

    private void ArrangeExistingParticipant(TranslationRoom room, Guid userId, string? status)
    {
        var participant = status == null
            ? null
            : new TranslationRoomParticipant
            {
                Id = Guid.CreateVersion7(),
                TranslationRoomId = room.Id,
                UserId = userId,
                DisplayName = "Returning User",
                Role = "PARTICIPANT",
                Status = status,
                SpeakLanguage = "en",
                ListenLanguage = "vi"
            };

        _mockParticipantRepo.Setup(p => p.GetByRoomAndUserAsync(room.Id, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(participant);
    }

    private Task<Result<JoinTranslationRoomResponse>> JoinAs(Guid userId) =>
        _service.JoinTranslationRoomAsync(new JoinTranslationRoomRequest(RoomCode, "User", "en", "vi"), userId);

    [Fact]
    public async Task Join_IsRejected_WhenTheRoomIsAtCapacity()
    {
        var room = ArrangeRoom(Guid.NewGuid(), maxParticipants: 2);
        var newcomer = Guid.NewGuid();
        ArrangeExistingParticipant(room, newcomer, status: null);
        ArrangeSeatsTaken(room, 2);

        var result = await JoinAs(newcomer);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
        _mockParticipantRepo.Verify(
            p => p.AddAsync(It.IsAny<TranslationRoomParticipant>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Join_IsAllowed_WhenOneSeatRemains()
    {
        var room = ArrangeRoom(Guid.NewGuid(), maxParticipants: 3);
        var newcomer = Guid.NewGuid();
        ArrangeExistingParticipant(room, newcomer, status: null);
        ArrangeSeatsTaken(room, 2);

        var result = await JoinAs(newcomer);

        result.IsSuccess.Should().BeTrue();
    }

    /// <summary>The host is the one person who cannot route around a full room, and locking them
    /// out would strand every guest already inside it.</summary>
    [Fact]
    public async Task Host_IsNeverLockedOutOfTheirOwnRoom()
    {
        var hostId = Guid.NewGuid();
        var room = ArrangeRoom(hostId, maxParticipants: 2);
        ArrangeExistingParticipant(room, hostId, status: null);
        ArrangeSeatsTaken(room, 99);

        var result = await JoinAs(hostId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Participant.Role.Should().Be("HOST");
    }

    /// <summary>A CONNECTED participant re-issuing join is re-entering on the seat they already
    /// hold, so they must not be counted a second time and turned away from a room they are in.</summary>
    [Fact]
    public async Task SeatHoldingParticipant_IsNotRejected_WhenTheRoomIsFull()
    {
        var room = ArrangeRoom(Guid.NewGuid(), maxParticipants: 2);
        var returning = Guid.NewGuid();
        ArrangeExistingParticipant(room, returning, TranslationRoomParticipantStatuses.Connected);
        ArrangeSeatsTaken(room, 2);

        var result = await JoinAs(returning);

        result.IsSuccess.Should().BeTrue();
        _mockParticipantRepo.Verify(
            p => p.CountSeatHoldingParticipantsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task MaxParticipantsAtOrBelowZero_MeansUnlimited(int maxParticipants)
    {
        var room = ArrangeRoom(Guid.NewGuid(), maxParticipants);
        var newcomer = Guid.NewGuid();
        ArrangeExistingParticipant(room, newcomer, status: null);
        ArrangeSeatsTaken(room, 5000);

        var result = await JoinAs(newcomer);

        result.IsSuccess.Should().BeTrue();
        // Consistent with how WorkspaceGrpcService treats MaxActiveRooms > 0: an unset cap is not
        // a cap, so the count is not even queried.
        _mockParticipantRepo.Verify(
            p => p.CountSeatHoldingParticipantsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>A participant who dropped released their seat, so they re-acquire one on return and
    /// are subject to the cap like anybody else.</summary>
    [Theory]
    [InlineData("LEFT")]
    [InlineData("DISCONNECTED")]
    [InlineData("WAITING")]
    public async Task NonSeatHoldingStatuses_MustReacquireASeat(string status)
    {
        var room = ArrangeRoom(Guid.NewGuid(), maxParticipants: 2);
        var returning = Guid.NewGuid();
        ArrangeExistingParticipant(room, returning, status);
        ArrangeSeatsTaken(room, 2);

        var result = await JoinAs(returning);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    [Fact]
    public void OnlyConnectedHoldsASeat()
    {
        TranslationRoomParticipantStatuses.HoldsSeat(TranslationRoomParticipantStatuses.Connected).Should().BeTrue();

        foreach (var status in new[]
                 {
                     TranslationRoomParticipantStatuses.Invited,
                     TranslationRoomParticipantStatuses.Waiting,
                     TranslationRoomParticipantStatuses.Disconnected,
                     TranslationRoomParticipantStatuses.Left,
                     TranslationRoomParticipantStatuses.Kicked,
                     TranslationRoomParticipantStatuses.Rejected
                 })
        {
            TranslationRoomParticipantStatuses.HoldsSeat(status).Should().BeFalse($"{status} does not occupy a seat");
        }

        TranslationRoomParticipantStatuses.HoldsSeat(null).Should().BeFalse();
    }
}
