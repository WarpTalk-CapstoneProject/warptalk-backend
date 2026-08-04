using Moq;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.Shared;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.LanguagePolicy;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

public class TranslationRoomServiceTests
{
    private readonly Mock<IUnitOfWork> _mockUow;
    private readonly Mock<ITranslationRoomRepository> _mockRoomRepo;
    private readonly Mock<ITranslationRoomParticipantRepository> _mockParticipantRepo;
    private readonly Mock<ITranslationRoomAudioRouteRepository> _mockAudioRouteRepo;
    private readonly Mock<ITranslationRoomSessionRepository> _mockSessionRepo;
    private readonly Mock<ILanguagePolicy> _mockLanguagePolicy;
    private readonly Mock<IAudioRouteEventProcessor> _mockAudioRouteEventProcessor;
    private readonly Mock<ITranslationRoomAudioRouteService> _mockAudioRouteService;
    private readonly Mock<IUserSettingsDirectory> _mockUserSettingsDirectory;
    private readonly Mock<WarpTalk.Shared.Interfaces.IEmailService> _mockEmailService;
    private readonly Mock<IRedisStateRepository> _mockRedisStateRepository;
    private readonly Mock<Microsoft.Extensions.Logging.ILogger<WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService>> _mockLogger;
    private readonly WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService _service;

    public TranslationRoomServiceTests()
    {
        _mockUow = new Mock<IUnitOfWork>();
        _mockRoomRepo = new Mock<ITranslationRoomRepository>();
        _mockParticipantRepo = new Mock<ITranslationRoomParticipantRepository>();
        _mockAudioRouteRepo = new Mock<ITranslationRoomAudioRouteRepository>();
        _mockSessionRepo = new Mock<ITranslationRoomSessionRepository>();
        _mockLanguagePolicy = new Mock<ILanguagePolicy>();
        _mockAudioRouteEventProcessor = new Mock<IAudioRouteEventProcessor>();
        _mockAudioRouteService = new Mock<ITranslationRoomAudioRouteService>();
        _mockUserSettingsDirectory = new Mock<IUserSettingsDirectory>();
        _mockEmailService = new Mock<WarpTalk.Shared.Interfaces.IEmailService>();
        _mockRedisStateRepository = new Mock<IRedisStateRepository>();
        _mockLogger = new Mock<Microsoft.Extensions.Logging.ILogger<WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService>>();

        _mockUow.Setup(u => u.TranslationRoomRepository).Returns(_mockRoomRepo.Object);
        _mockUow.Setup(u => u.TranslationRoomParticipantRepository).Returns(_mockParticipantRepo.Object);
        _mockUow.Setup(u => u.TranslationRoomAudioRouteRepository).Returns(_mockAudioRouteRepo.Object);
        _mockUow.Setup(u => u.TranslationRoomSessionRepository).Returns(_mockSessionRepo.Object);

        _mockParticipantRepo.Setup(p => p.GetByRoomIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomParticipant>());

        _mockAudioRouteRepo.Setup(r => r.GetRoutesByRoomIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomAudioRoute> { new TranslationRoomAudioRoute() });

        // Start (re)generates audio routes for the current roster; default to success.
        _mockAudioRouteService.Setup(s => s.GenerateRoutesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new List<TranslationRoomAudioRouteDto>()));

        _mockLanguagePolicy.Setup(v => v.IsSupportedAsync(It.IsAny<string>())).ReturnsAsync(true);
        _mockLanguagePolicy.Setup(v => v.ValidateParticipantLanguagesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TranslationRoom>())).ReturnsAsync((string?)null);

        _service = new WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService(
            _mockUow.Object,
            _mockLanguagePolicy.Object,
            _mockAudioRouteEventProcessor.Object,
            _mockAudioRouteService.Object,
            _mockUserSettingsDirectory.Object,
            _mockEmailService.Object,
            _mockLogger.Object,
            redisStateRepository: _mockRedisStateRepository.Object);
    }

    [Fact]
    public async Task GetTranslationRoomHistoryAsync_ShouldReject_WhenWorkspaceIdIsMissing()
    {
        var request = new GetTranslationRoomsRequest(Status: "ENDED,CANCELLED");

        var result = await _service.GetTranslationRoomHistoryAsync(request, Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        _mockUow.Verify(u => u.Repository<TranslationRoom>(), Times.Never);
    }

    [Fact]
    public void ApplyRoomFilters_ShouldReturnOnlyRequestedWorkspace()
    {
        var workspaceId = Guid.NewGuid();
        var rooms = new[]
        {
            new TranslationRoom { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Status = "ENDED" },
            new TranslationRoom { Id = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), Status = "ENDED" },
        }.AsQueryable();
        var request = new GetTranslationRoomsRequest(Status: "ENDED", WorkspaceId: workspaceId);
        var method = typeof(WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService)
            .GetMethod("ApplyRoomFilters", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var result = (IQueryable<TranslationRoom>)method.Invoke(null, [rooms, request])!;

        result.Select(room => room.WorkspaceId).Should().Equal(workspaceId);
    }

    [Fact]
    public async Task JoinTranslationRoomAsync_ShouldAssignHostRole_WhenUserIsHost()
    {
        // Arrange
        var hostId = Guid.NewGuid();
        var roomCode = "abc-defg-hij";
        var room = new TranslationRoom
        {
            Id = Guid.NewGuid(),
            HostId = hostId,
            TranslationRoomCode = roomCode,
            Status = "WAITING",
            TranslationRoomType = "INSTANT".ToString(),
            Settings = "{\"requires_approval\":true,\"history_access\":\"HostOnly\"}"
        };

        var request = new JoinTranslationRoomRequest(roomCode, "Host User", "en", "vi");

        _mockRoomRepo.Setup(r => r.GetByCodeAsync(roomCode, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        _mockParticipantRepo.Setup(p => p.GetByRoomAndUserAsync(room.Id, hostId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomParticipant?)null);

        // Act
        var result = await _service.JoinTranslationRoomAsync(request, hostId);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.Participant.Role.Should().Be("HOST");
        _mockParticipantRepo.Verify(p => p.AddAsync(It.Is<TranslationRoomParticipant>(pt => pt.Role == "HOST".ToString()), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JoinTranslationRoomAsync_ShouldReject_WhenRoomStatusIsEnded()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var roomCode = "abc-defg-hij";
        var room = new TranslationRoom
        {
            Id = Guid.NewGuid(),
            TranslationRoomCode = roomCode,
            Status = "ENDED",
            TranslationRoomType = "INSTANT".ToString(),
            Settings = "{\"requires_approval\":true,\"history_access\":\"HostOnly\"}"
        };

        var request = new JoinTranslationRoomRequest(roomCode, "User", "en", "vi");

        _mockRoomRepo.Setup(r => r.GetByCodeAsync(roomCode, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        // Act
        var result = await _service.JoinTranslationRoomAsync(request, userId);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidState);
    }

    [Fact]
    public async Task JoinTranslationRoomAsync_ShouldUpdateParticipant_WhenAlreadyExists()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var roomCode = "abc-defg-hij";
        var roomId = Guid.NewGuid();
        var room = new TranslationRoom
        {
            Id = roomId,
            TranslationRoomCode = roomCode,
            Status = "WAITING",
            HostId = Guid.NewGuid(),
            TranslationRoomType = "INSTANT".ToString(),
            Settings = "{\"requires_approval\":false}"
        };

        var existingParticipant = new TranslationRoomParticipant
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = roomId,
            UserId = userId,
            DisplayName = "Old Name",
            Role = "PARTICIPANT".ToString(),
            Status = "INVITED"
        };

        var request = new JoinTranslationRoomRequest(roomCode, "New Name", "fr", "es");

        _mockRoomRepo.Setup(r => r.GetByCodeAsync(roomCode, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        _mockParticipantRepo.Setup(p => p.GetByRoomAndUserAsync(roomId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingParticipant);

        // Act
        var result = await _service.JoinTranslationRoomAsync(request, userId);

        // Assert
        result.IsSuccess.Should().BeTrue();
        existingParticipant.DisplayName.Should().Be("New Name");
        existingParticipant.SpeakLanguage.Should().Be("fr");
        existingParticipant.ListenLanguage.Should().Be("es");
        existingParticipant.Status.Should().Be("CONNECTED");
        _mockParticipantRepo.Verify(p => p.Update(existingParticipant), Times.Once);
    }

    [Fact]
    public async Task JoinTranslationRoomAsync_ShouldKeepPriorApproval_WhenAdmittedParticipantRejoins()
    {
        var userId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var roomCode = "approved-rejoin";
        var room = new TranslationRoom
        {
            Id = roomId,
            TranslationRoomCode = roomCode,
            Status = "WAITING",
            HostId = Guid.NewGuid(),
            TranslationRoomType = "INSTANT",
            Settings = "{\"requires_approval\":true}"
        };
        var existingParticipant = new TranslationRoomParticipant
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = roomId,
            UserId = userId,
            DisplayName = "Approved User",
            Role = "PARTICIPANT",
            Status = "LEFT",
            LeftAt = DateTime.UtcNow
        };
        var request = new JoinTranslationRoomRequest(
            roomCode,
            "Approved User",
            "en",
            "vi");

        _mockRoomRepo
            .Setup(r => r.GetByCodeAsync(
                roomCode,
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        _mockParticipantRepo
            .Setup(p => p.GetByRoomAndUserAsync(
                roomId,
                userId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingParticipant);

        var result = await _service.JoinTranslationRoomAsync(request, userId);

        result.IsSuccess.Should().BeTrue();
        existingParticipant.Status.Should().Be("CONNECTED");
        existingParticipant.LeftAt.Should().BeNull();
    }

    [Fact]
    public async Task JoinTranslationRoomAsync_ShouldStillRequireApproval_ForNeverAdmittedInvitee()
    {
        var userId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var roomCode = "first-approval";
        var room = new TranslationRoom
        {
            Id = roomId,
            TranslationRoomCode = roomCode,
            Status = "WAITING",
            HostId = Guid.NewGuid(),
            TranslationRoomType = "INSTANT",
            Settings = "{\"requires_approval\":true}"
        };
        var existingParticipant = new TranslationRoomParticipant
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = roomId,
            UserId = userId,
            DisplayName = "New Invitee",
            Role = "PARTICIPANT",
            Status = "INVITED"
        };
        var request = new JoinTranslationRoomRequest(
            roomCode,
            "New Invitee",
            "en",
            "vi");

        _mockRoomRepo
            .Setup(r => r.GetByCodeAsync(
                roomCode,
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        _mockParticipantRepo
            .Setup(p => p.GetByRoomAndUserAsync(
                roomId,
                userId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingParticipant);

        var result = await _service.JoinTranslationRoomAsync(request, userId);

        result.IsSuccess.Should().BeTrue();
        existingParticipant.Status.Should().Be("WAITING");
    }

    [Fact]
    public async Task StartTranslationRoomAsync_ValidState_UpdatesStatusAndFiresEvent()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = new TranslationRoom
        {
            Id = roomId,
            WorkspaceId = Guid.NewGuid(),
            HostId = hostId,
            Title = "Test room",
            TranslationRoomCode = "ABC-DEF-GHI",
            Status = "WAITING",
            TranslationRoomType = "INSTANT".ToString(),
            MaxParticipants = 10,
            SourceLanguage = "vi",
            TargetLanguages = "[\"en\"]",
            CreatedAt = DateTime.UtcNow,
            Settings = "{\"requires_approval\":true,\"artifact_access\":\"HostOnly\"}"
        };

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(room);

        var result = await _service.StartTranslationRoomAsync(roomId, hostId);

        result.IsSuccess.Should().BeTrue(result.Error);
        room.Status.Should().Be("IN_PROGRESS");
        room.StartedAt.Should().NotBeNull();
        _mockAudioRouteEventProcessor.Verify(a => a.ProcessEventAsync(roomId, null, AudioRoutingEventType.session_starts.ToString(), "{}", default), Times.Once);
    }

    [Fact]
    public async Task StartTranslationRoomAsync_InvalidState_ReturnsError()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = new TranslationRoom
        {
            Id = roomId,
            WorkspaceId = Guid.NewGuid(),
            HostId = hostId,
            Title = "Ended room",
            TranslationRoomCode = "ABC-DEF-GHI",
            Status = "ENDED",
            TranslationRoomType = "INSTANT".ToString(),
            MaxParticipants = 10,
            SourceLanguage = "vi",
            TargetLanguages = "[\"en\"]",
            CreatedAt = DateTime.UtcNow,
            Settings = "{\"requires_approval\":true,\"artifact_access\":\"HostOnly\"}"
        };

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(room);

        var result = await _service.StartTranslationRoomAsync(roomId, hostId);

        result.IsSuccess.Should().BeFalse(result.Error);
        result.Error.Should().Be(TranslationRoomConstants.ErrorInvalidTransitionToStart);
        _mockAudioRouteEventProcessor.Verify(a => a.ProcessEventAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task StartTranslationRoomAsync_NoPreexistingRoutes_GeneratesRoutesAndStarts()
    {
        // A host must be able to start the room before other participants join. Routes form a
        // full mesh between participants whose languages differ, so a host-only room has zero
        // routes — Start must (re)generate routes for the current roster and still transition to
        // IN_PROGRESS instead of failing with a 409.
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = new TranslationRoom
        {
            Id = roomId,
            WorkspaceId = Guid.NewGuid(),
            HostId = hostId,
            Title = "Test room",
            TranslationRoomCode = "ABC-DEF-GHI",
            Status = "WAITING",
            TranslationRoomType = "INSTANT".ToString(),
            MaxParticipants = 10,
            SourceLanguage = "vi",
            TargetLanguages = "[\"en\"]",
            CreatedAt = DateTime.UtcNow,
            Settings = "{\"requires_approval\":true,\"artifact_access\":\"HostOnly\"}"
        };

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(room);
        _mockAudioRouteRepo.Setup(r => r.GetRoutesByRoomIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomAudioRoute>());

        var result = await _service.StartTranslationRoomAsync(roomId, hostId);

        result.IsSuccess.Should().BeTrue(result.Error);
        room.Status.Should().Be("IN_PROGRESS");
        _mockAudioRouteService.Verify(s => s.GenerateRoutesAsync(roomId, It.IsAny<CancellationToken>()), Times.Once);
        _mockAudioRouteEventProcessor.Verify(a => a.ProcessEventAsync(roomId, null, AudioRoutingEventType.session_starts.ToString(), "{}", default), Times.Once);
    }

    [Fact]
    public async Task EndTranslationRoomAsync_SetsEndedAtWithoutPersistingDurationAndFiresEvent()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var startedAt = DateTime.UtcNow.AddMinutes(-30);
        var room = new TranslationRoom { Id = roomId, HostId = hostId, Status = "IN_PROGRESS", StartedAt = startedAt, Settings = "{\"requires_approval\":true,\"history_access\":\"HostOnly\"}" };

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(room);
        _mockParticipantRepo.Setup(p => p.GetByRoomIdAsync(roomId, default)).ReturnsAsync(new List<TranslationRoomParticipant>());

        var result = await _service.EndTranslationRoomAsync(roomId, hostId);

        result.IsSuccess.Should().BeTrue();
        room.Status.Should().Be("ENDED");
        room.EndedAt.Should().NotBeNull();
        room.DurationSeconds.Should().BeNull();
        _mockAudioRouteEventProcessor.Verify(a => a.ProcessEventAsync(roomId, null, AudioRoutingEventType.session_ends.ToString(), "{}", default), Times.Once);
    }

    // WT-191 — participants used to sit in an ended room until they pressed Leave, because
    // ending over REST never reached the SignalR hub in the Gateway process.

    [Fact]
    public async Task EndTranslationRoomAsync_PublishesRoomEndedToTheGatewayRelay()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = new TranslationRoom { Id = roomId, HostId = hostId, Status = "IN_PROGRESS", Settings = "{\"requires_approval\":true}" };

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(room);
        _mockParticipantRepo.Setup(p => p.GetByRoomIdAsync(roomId, default)).ReturnsAsync(new List<TranslationRoomParticipant>());

        var result = await _service.EndTranslationRoomAsync(roomId, hostId);

        result.IsSuccess.Should().BeTrue();
        _mockRedisStateRepository.Verify(
            r => r.PublishAsync(
                "warptalk:translation-room:commands",
                It.Is<string>(payload =>
                    payload.Contains("\"Command\":\"RoomEnded\"")
                    && payload.Contains(roomId.ToString()))),
            Times.Once);
    }

    [Fact]
    public async Task EndTranslationRoomAsync_StillEndsTheRoom_WhenTheRelayPublishFails()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = new TranslationRoom { Id = roomId, HostId = hostId, Status = "IN_PROGRESS", Settings = "{\"requires_approval\":true}" };

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(room);
        _mockParticipantRepo.Setup(p => p.GetByRoomIdAsync(roomId, default)).ReturnsAsync(new List<TranslationRoomParticipant>());
        _mockRedisStateRepository
            .Setup(r => r.PublishAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("redis is down"));

        var result = await _service.EndTranslationRoomAsync(roomId, hostId);

        // The room is already ENDED and persisted; a dead relay must not turn that into a failure.
        result.IsSuccess.Should().BeTrue();
        room.Status.Should().Be("ENDED");
        _mockAudioRouteEventProcessor.Verify(
            a => a.ProcessEventAsync(roomId, null, AudioRoutingEventType.session_ends.ToString(), "{}", default),
            Times.Once);
    }

    // WT-187 — inviting someone wrote invitation rows and sent an email, but published nothing,
    // so the invitee's rooms list stayed stale until they reloaded the page by hand.

    private const string MeetingEventsChannel = "warptalk:meetings:events";

    private Mock<IGenericRepository<TranslationRoomInvitation>> ArrangeInvitationRepo(
        params TranslationRoomInvitation[] existing)
    {
        var repo = new Mock<IGenericRepository<TranslationRoomInvitation>>();
        repo.Setup(r => r.FindAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<TranslationRoomInvitation, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _mockUow.Setup(u => u.Repository<TranslationRoomInvitation>()).Returns(repo.Object);
        return repo;
    }

    [Fact]
    public async Task CreateTranslationRoomAsync_PublishesMeetingInvited_WhenTheRoomIsCreatedWithInvitees()
    {
        var workspaceId = Guid.NewGuid();
        ArrangeInvitationRepo();
        _mockRoomRepo
            .Setup(r => r.ExistsByCodeAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var request = new CreateTranslationRoomRequest(
            workspaceId, "Standup", null, "INSTANT", 10,
            "vi-VN", new List<string> { "en-US" }, null, null,
            new List<string> { "invitee@warptalk.io.vn" });

        var result = await _service.CreateTranslationRoomAsync(request, Guid.NewGuid());

        result.IsSuccess.Should().BeTrue();
        _mockRedisStateRepository.Verify(
            r => r.PublishAsync(
                MeetingEventsChannel,
                It.Is<string>(payload =>
                    // camelCase keys — the Gateway reads them with a case-sensitive
                    // TryGetProperty, so PascalCase here would be silently ignored.
                    payload.Contains("\"eventType\":\"MeetingInvited\"")
                    && payload.Contains($"\"workspaceId\":\"{workspaceId}\""))),
            Times.Once);
    }

    [Fact]
    public async Task CreateTranslationRoomAsync_DoesNotPublish_WhenNobodyIsInvited()
    {
        _mockRoomRepo
            .Setup(r => r.ExistsByCodeAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var request = new CreateTranslationRoomRequest(
            Guid.NewGuid(), "Solo", null, "INSTANT", 10,
            "vi-VN", new List<string> { "en-US" }, null, null, null);

        var result = await _service.CreateTranslationRoomAsync(request, Guid.NewGuid());

        result.IsSuccess.Should().BeTrue();
        _mockRedisStateRepository.Verify(
            r => r.PublishAsync(MeetingEventsChannel, It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateTranslationRoomAsync_StillCreatesTheRoom_WhenTheInviteEventPublishFails()
    {
        ArrangeInvitationRepo();
        _mockRoomRepo
            .Setup(r => r.ExistsByCodeAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _mockRedisStateRepository
            .Setup(r => r.PublishAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("redis is down"));

        var request = new CreateTranslationRoomRequest(
            Guid.NewGuid(), "Standup", null, "INSTANT", 10,
            "vi-VN", new List<string> { "en-US" }, null, null,
            new List<string> { "invitee@warptalk.io.vn" });

        var result = await _service.CreateTranslationRoomAsync(request, Guid.NewGuid());

        // The invitations are committed and the emails are out; a dead relay only costs the
        // invitee a live refresh, so it must not fail the create.
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateTranslationRoomSettingsAsync_PublishesMeetingInvited_ForANewlyAddedInvitee()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var room = new TranslationRoom
        {
            Id = roomId,
            WorkspaceId = workspaceId,
            HostId = hostId,
            Status = "WAITING",
            Title = "Standup",
            TranslationRoomCode = "abc-defg-hij",
            Settings = "{\"requires_approval\":true}"
        };

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, It.IsAny<CancellationToken>())).ReturnsAsync(room);
        ArrangeInvitationRepo();

        var request = new UpdateRoomSettingsRequest(
            null, null, null, null,
            new List<string> { "invitee@warptalk.io.vn" },
            null, null, null);

        var result = await _service.UpdateTranslationRoomSettingsAsync(roomId, hostId, request);

        result.IsSuccess.Should().BeTrue();
        _mockRedisStateRepository.Verify(
            r => r.PublishAsync(
                MeetingEventsChannel,
                It.Is<string>(payload =>
                    payload.Contains("\"eventType\":\"MeetingInvited\"")
                    && payload.Contains($"\"workspaceId\":\"{workspaceId}\"")
                    && payload.Contains(roomId.ToString()))),
            Times.Once);
    }

    [Fact]
    public async Task UpdateTranslationRoomSettingsAsync_DoesNotPublish_WhenEveryInviteeWasAlreadyInvited()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = new TranslationRoom
        {
            Id = roomId,
            WorkspaceId = Guid.NewGuid(),
            HostId = hostId,
            Status = "WAITING",
            Title = "Standup",
            TranslationRoomCode = "abc-defg-hij",
            Settings = "{\"requires_approval\":true}"
        };

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, It.IsAny<CancellationToken>())).ReturnsAsync(room);
        // Case-insensitive on purpose: the dedupe uses OrdinalIgnoreCase, so re-submitting the
        // same address in a different case must stay a no-op on the wire too.
        ArrangeInvitationRepo(new TranslationRoomInvitation
        {
            TranslationRoomId = roomId,
            Email = "Invitee@WarpTalk.io.vn",
            Status = "PENDING"
        });

        var request = new UpdateRoomSettingsRequest(
            null, null, null, null,
            new List<string> { "invitee@warptalk.io.vn" },
            null, null, null);

        var result = await _service.UpdateTranslationRoomSettingsAsync(roomId, hostId, request);

        result.IsSuccess.Should().BeTrue();
        _mockEmailService.Verify(
            e => e.SendMeetingInvitationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockRedisStateRepository.Verify(
            r => r.PublishAsync(MeetingEventsChannel, It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task ExpireTranslationRoomAsync_Idempotent_ReturnsSuccess()
    {
        var roomId = Guid.NewGuid();
        var room = new TranslationRoom { Id = roomId, Status = "EXPIRED", Settings = "{\"requires_approval\":true}" };

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(room);

        var result = await _service.ExpireTranslationRoomAsync(roomId);

        result.IsSuccess.Should().BeTrue();
        _mockRoomRepo.Verify(r => r.Update(It.IsAny<TranslationRoom>()), Times.Never);
    }

    [Fact]
    public async Task UpdateTranslationRoomSettingsAsync_ShouldUpdateRequiresApproval()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = new TranslationRoom
        {
            Id = roomId,
            HostId = hostId,
            Status = "WAITING",
            Settings = "{\"requires_approval\":true}"
        };

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(room);

        var request = new UpdateRoomSettingsRequest(
            Title: null,
            Description: null,
            MaxParticipants: null,
            ScheduledAt: null,
            InvitedEmails: null,
            Settings: new RoomSettingsRequest(false),
            SourceLanguage: "en",
            TargetLanguages: new List<string> { "vi" }
        );

        // Act
        var result = await _service.UpdateTranslationRoomSettingsAsync(roomId, hostId, request);

        // Assert
        result.IsSuccess.Should().BeTrue();
        room.Settings.Should().Contain("requires_approval\":false");
    }

    [Fact]
    public async Task EndTranslationRoomAsync_ShouldDisconnectConnectedAndWaitingParticipants()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = new TranslationRoom { Id = roomId, HostId = hostId, Status = "IN_PROGRESS", StartedAt = DateTime.UtcNow, Settings = "{\"requires_approval\":true}" };

        var participant1 = new TranslationRoomParticipant { Status = "CONNECTED" };
        var participant2 = new TranslationRoomParticipant { Status = "WAITING" };
        var participant3 = new TranslationRoomParticipant { Status = "INVITED" };

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(room);
        _mockParticipantRepo.Setup(p => p.GetByRoomIdAsync(roomId, default))
            .ReturnsAsync(new List<TranslationRoomParticipant> { participant1, participant2, participant3 });

        // Act
        var result = await _service.EndTranslationRoomAsync(roomId, hostId);

        // Assert
        result.IsSuccess.Should().BeTrue();
        participant1.Status.Should().Be("DISCONNECTED");
        participant2.Status.Should().Be("DISCONNECTED");
        participant3.Status.Should().Be("INVITED"); // unchanged
        _mockParticipantRepo.Verify(p => p.Update(It.IsAny<TranslationRoomParticipant>()), Times.Exactly(2));
    }
}
