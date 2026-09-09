using System;
using System.Linq.Expressions;
using System.Threading;
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
    private readonly Mock<IWorkspaceMeetingPolicy> _mockWorkspaceMeetingPolicy;
    private readonly Mock<IWorkspaceMemberDirectory> _mockWorkspaceMemberDirectory;
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
        _mockWorkspaceMeetingPolicy = new Mock<IWorkspaceMeetingPolicy>();
        _mockWorkspaceMemberDirectory = new Mock<IWorkspaceMemberDirectory>();
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

        // The workspace permits meeting creation unless a test says otherwise (WT-249).
        _mockWorkspaceMeetingPolicy.Setup(p => p.ValidateMeetingCreationAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        // ...and the tenant itself is live unless a test suspends it.
        _mockWorkspaceMeetingPolicy.Setup(p => p.EnsureWorkspaceCanHostMeetingsAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        _service = new WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService(
            _mockUow.Object,
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

    /// <summary>
    /// Stop Translation must leave the MEETING alone. Pausing the room was the old implementation
    /// and it took the transcript down with the translation, because the AI workers read a PAUSED
    /// room as one whose microphone to ignore.
    /// </summary>
    [Fact]
    public async Task StopTranslationAsync_EndsTheSession_AndLeavesTheRoomInProgress()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = new TranslationRoom { Id = roomId, HostId = hostId, Status = "IN_PROGRESS" };
        var session = new TranslationRoomSession
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = roomId,
            Status = TranslationRoomSessionStatus.ACTIVE.ToString()
        };

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, It.IsAny<CancellationToken>())).ReturnsAsync(room);
        _mockSessionRepo.Setup(r => r.GetActiveSessionByRoomIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        var result = await _service.StopTranslationAsync(roomId, hostId);

        result.IsSuccess.Should().BeTrue();
        room.Status.Should().Be("IN_PROGRESS");
        session.Status.Should().Be(TranslationRoomSessionStatus.ENDED.ToString());
        session.EndedAt.Should().NotBeNull();

        _mockAudioRouteEventProcessor.Verify(p => p.ProcessEventAsync(
            roomId,
            null,
            AudioRoutingEventType.translation_stopped.ToString(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // The event that would have stopped transcription too.
        _mockAudioRouteEventProcessor.Verify(p => p.ProcessEventAsync(
            It.IsAny<Guid>(),
            It.IsAny<Guid?>(),
            AudioRoutingEventType.room_pause.ToString(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);

        // Start and Stop are room-wide, so everyone in the meeting is told — not just the host
        // who pressed it, and not only whenever each client's own poll next comes round.
        _mockRedisStateRepository.Verify(r => r.PublishAsync(
            "warptalk:translation-room:commands",
            It.Is<string>(payload => payload.Contains("TranslationStopped") && payload.Contains(roomId.ToString()))),
            Times.Once);
    }

    [Fact]
    public async Task StopTranslationAsync_RejectsANonHost()
    {
        var roomId = Guid.NewGuid();
        var room = new TranslationRoom { Id = roomId, HostId = Guid.NewGuid(), Status = "IN_PROGRESS" };
        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, It.IsAny<CancellationToken>())).ReturnsAsync(room);

        var result = await _service.StopTranslationAsync(roomId, Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Unauthorized);
        _mockAudioRouteEventProcessor.Verify(p => p.ProcessEventAsync(
            It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetTranslationRoomHistoryAsync_ShouldReject_WhenWorkspaceIdIsMissing()
    {
        var request = new GetTranslationRoomsRequest(Status: "ENDED,CANCELLED");

        var result = await _service.GetTranslationRoomHistoryAsync(request, Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        // Asserted on Query() rather than on the repository property: the constructor resolves
        // that property, so "never fetched the repository" stopped meaning "never ran the query"
        // once the history path moved off the generic Repository<T>() factory.
        _mockRoomRepo.Verify(r => r.Query(), Times.Never);
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

        var result = await _service.StartTranslationRoomAsync(roomId, hostId, null);

        result.IsSuccess.Should().BeTrue(result.Error);
        room.Status.Should().Be("IN_PROGRESS");
        room.StartedAt.Should().NotBeNull();
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

        var result = await _service.StartTranslationRoomAsync(roomId, hostId, null);

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

        var result = await _service.StartTranslationRoomAsync(roomId, hostId, null);

        result.IsSuccess.Should().BeTrue(result.Error);
        room.Status.Should().Be("IN_PROGRESS");
        _mockAudioRouteService.Verify(s => s.GenerateRoutesAsync(roomId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResumeTranslationRoomAsync_PublishesRoomStartedToTheGatewayRelay()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = NewStartableRoom(roomId, hostId);
        room.Status = "IN_PROGRESS";

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(room);

        var result = await _service.ResumeTranslationRoomAsync(roomId, hostId);

        result.IsSuccess.Should().BeTrue(result.Error);
        _mockRedisStateRepository.Verify(
            r => r.PublishAsync(
                "warptalk:translation-room:commands",
                It.Is<string>(payload =>
                    payload.Contains("\"Command\":\"RoomStarted\"")
                    && payload.Contains(roomId.ToString()))),
            Times.Once);
        _mockAudioRouteEventProcessor.Verify(
            a => a.ProcessEventAsync(roomId, null, AudioRoutingEventType.room_resume.ToString(), "{}", default),
            Times.Once);
    }

    [Fact]
    public async Task ResumeTranslationRoomAsync_RoomStartedCarriesTheStateTheClientBindsTo()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var connectedUserId = Guid.NewGuid();
        var room = NewStartableRoom(roomId, hostId);
        room.Status = "IN_PROGRESS";

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(room);
        _mockParticipantRepo
            .Setup(p => p.GetByRoomIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomParticipant>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    TranslationRoomId = roomId,
                    UserId = connectedUserId,
                    DisplayName = "Already in the room",
                    Role = "participant",
                    SpeakLanguage = "vi",
                    ListenLanguage = "en",
                    Status = TranslationRoomParticipantStatuses.Connected,
                    ConnectionType = "WEB",
                    JoinedAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    TranslationRoomId = roomId,
                    UserId = Guid.NewGuid(),
                    DisplayName = "Still in the lobby",
                    Role = "participant",
                    SpeakLanguage = "en",
                    ListenLanguage = "vi",
                    Status = TranslationRoomParticipantStatuses.Waiting,
                    ConnectionType = "WEB",
                    CreatedAt = DateTime.UtcNow
                }
            });

        var result = await _service.ResumeTranslationRoomAsync(roomId, hostId);

        result.IsSuccess.Should().BeTrue(result.Error);

        var published = _mockRedisStateRepository.Invocations
            .Where(i => i.Method.Name == nameof(IRedisStateRepository.PublishAsync))
            .Select(i => (string)i.Arguments[1])
            .Single(p => p.Contains("\"Command\":\"RoomStarted\""));

        using var document = System.Text.Json.JsonDocument.Parse(published);
        var state = document.RootElement.GetProperty("State");
        state.GetProperty("translationRoomId").GetString().Should().Be(roomId.ToString());
        state.GetProperty("translationRoomCode").GetString().Should().Be("ABC-DEF-GHI");
        state.GetProperty("status").GetString().Should().Be("IN_PROGRESS");

        var participants = state.GetProperty("participants");
        participants.GetArrayLength().Should().Be(1);
        participants[0].GetProperty("userId").GetString().Should().Be(connectedUserId.ToString());
        participants[0].GetProperty("displayName").GetString().Should().Be("Already in the room");
        participants[0].GetProperty("speakLanguage").GetString().Should().Be("vi");
        participants[0].GetProperty("listenLanguage").GetString().Should().Be("en");

        participants[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "userId", "displayName", "speakLanguage", "listenLanguage", "isMuted", "joinedAt");
    }

    [Fact]
    public async Task ResumeTranslationRoomAsync_StillStartsTranslation_WhenTheRelayPublishFails()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = NewStartableRoom(roomId, hostId);
        room.Status = "IN_PROGRESS";

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(room);
        _mockRedisStateRepository
            .Setup(r => r.PublishAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("redis is down"));

        var result = await _service.ResumeTranslationRoomAsync(roomId, hostId);

        result.IsSuccess.Should().BeTrue(result.Error);
        room.Status.Should().Be("IN_PROGRESS");
        _mockAudioRouteEventProcessor.Verify(
            a => a.ProcessEventAsync(roomId, null, AudioRoutingEventType.room_resume.ToString(), "{}", default),
            Times.Once);
    }

    // ── Who may press Start Translation (WT-373) ──────────────────────────────────────────────
    //
    // /resume is the only path that opens a TranslationRoomSession, and that row IS
    // `translation_active` in PublishRoutesUpdateAsync — the flag the AI translation worker gates
    // every STT result on. So an authorization answer here is not "a 401 the user retries": it is
    // whether the meeting produces dubbed audio at all.
    //
    // WT-371 opened starting to participants and implemented the rule in
    // TranslationRoomSessionService.CanStartSessionAsync, which serves POST /sessions — an
    // endpoint the client does not call. This method kept a bare IsHostedBy, so the rule was
    // enforced where nothing runs and ignored where everything does. Nothing covered this path.

    [Fact]
    public async Task ResumeTranslationRoomAsync_ShouldLetAParticipantStart_WhenTheRoomOptedIn()
    {
        // The reported WT-373 case: the control bar offers the button on exactly this setting, so
        // before the fix the user was shown a button that could only answer 401.
        var roomId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var room = NewStartableRoom(roomId, Guid.NewGuid());
        room.Status = "IN_PROGRESS";
        room.Settings = "{\"participants_can_start_translation\":true}";

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(room);
        _mockParticipantRepo
            .Setup(r => r.AnyAsync(It.IsAny<Expression<Func<TranslationRoomParticipant, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _service.ResumeTranslationRoomAsync(roomId, participantId);

        result.IsSuccess.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task ResumeTranslationRoomAsync_ShouldRefuseAParticipant_WhenTheRoomHasNotOptedIn()
    {
        // The default. Opening translation to the room is a choice a host makes per room.
        var roomId = Guid.NewGuid();
        var room = NewStartableRoom(roomId, Guid.NewGuid());
        room.Status = "IN_PROGRESS";

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(room);
        _mockParticipantRepo
            .Setup(r => r.AnyAsync(It.IsAny<Expression<Func<TranslationRoomParticipant, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _service.ResumeTranslationRoomAsync(roomId, Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task ResumeTranslationRoomAsync_ShouldRefuseAStranger_EvenWhenTheRoomOptedIn()
    {
        // The setting opens translation to the ROOM, not to anyone holding its id. Without the
        // membership clause it would let any authenticated stranger start billable AI in it.
        var roomId = Guid.NewGuid();
        var room = NewStartableRoom(roomId, Guid.NewGuid());
        room.Status = "IN_PROGRESS";
        room.Settings = "{\"participants_can_start_translation\":true}";

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(room);
        _mockParticipantRepo
            .Setup(r => r.AnyAsync(It.IsAny<Expression<Func<TranslationRoomParticipant, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _service.ResumeTranslationRoomAsync(roomId, Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task ResumeTranslationRoomAsync_ShouldStillLetTheHostStart_WithoutConsultingWorkspaceService()
    {
        // Host identity is checked first on purpose: the host path must not depend on
        // WorkspaceService being reachable, and must not cost a gRPC hop per press.
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = NewStartableRoom(roomId, hostId);
        room.Status = "IN_PROGRESS";

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(room);

        var result = await _service.ResumeTranslationRoomAsync(roomId, hostId);

        result.IsSuccess.Should().BeTrue(result.Error);
        _mockWorkspaceMemberDirectory.Verify(
            d => d.IsOwnerOrAdminAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static TranslationRoom NewStartableRoom(Guid roomId, Guid hostId) => new()
    {
        Id = roomId,
        WorkspaceId = Guid.NewGuid(),
        HostId = hostId,
        Title = "Test room",
        TranslationRoomCode = "ABC-DEF-GHI",
        Status = "WAITING",
        TranslationRoomType = "INSTANT",
        MaxParticipants = 10,
        SourceLanguage = "vi",
        TargetLanguages = "[\"en\"]",
        CreatedAt = DateTime.UtcNow,
        Settings = "{\"requires_approval\":true,\"artifact_access\":\"HostOnly\"}"
    };

    /// <summary>
    /// Ending is a two-call client-side saga: "End for everyone" calls MeetingService and then this
    /// endpoint. MeetingRoomService.EndMeetingAsync accepts the ACTIVE host
    /// (isOriginalHost || isActiveHost) while this accepted only the ORIGINAL one — so after a host
    /// transfer the first call tore down LiveKit and marked the meeting FINISHED, the second was
    /// refused, and the translation room stayed IN_PROGRESS forever. Nothing repairs that:
    /// ExpireTranslationRoomAsync has no production callers.
    ///
    /// The rule here is now RoomHostAccess — host OR workspace Owner/Admin — which is what WT-188
    /// established and WT-313 reconciled, so an orphaned room is always recoverable by an
    /// Owner/Admin instead of being permanent.
    /// </summary>
    [Fact]
    public async Task EndTranslationRoomAsync_ShouldEnd_WhenRequesterIsAWorkspaceOwnerOrAdmin()
    {
        var roomId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var room = new TranslationRoom
        {
            Id = roomId,
            HostId = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Status = "IN_PROGRESS",
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            Settings = "{}"
        };

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(room);
        _mockParticipantRepo.Setup(p => p.GetByRoomIdAsync(roomId, default)).ReturnsAsync(new List<TranslationRoomParticipant>());
        _mockWorkspaceMemberDirectory
            .Setup(d => d.IsOwnerOrAdminAsync(workspaceId, admin, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _service.EndTranslationRoomAsync(roomId, admin);

        result.IsSuccess.Should().BeTrue(result.Error);
        room.Status.Should().Be("ENDED");
    }

    /// <summary>The widening stops at Owner/Admin: an unrelated user still cannot end a meeting.</summary>
    [Fact]
    public async Task EndTranslationRoomAsync_ShouldRefuse_WhenRequesterIsNeitherHostNorOwnerAdmin()
    {
        var roomId = Guid.NewGuid();
        var room = new TranslationRoom
        {
            Id = roomId,
            HostId = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            Status = "IN_PROGRESS",
            Settings = "{}"
        };

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(room);

        var result = await _service.EndTranslationRoomAsync(roomId, Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        room.Status.Should().Be("IN_PROGRESS");
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

    private Mock<ITranslationRoomInvitationRepository> ArrangeInvitationRepo(
        params TranslationRoomInvitation[] existing)
    {
        var repo = new Mock<ITranslationRoomInvitationRepository>();
        repo.Setup(r => r.FindAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<TranslationRoomInvitation, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _mockUow.Setup(u => u.TranslationRoomInvitationRepository).Returns(repo.Object);
        return repo;
    }

    // WT-249 — WorkspaceService has always exposed ValidateMeetingCreation, but nothing called it,
    // so revoking a member's host permission did not stop them opening rooms.

    [Fact]
    public async Task CreateTranslationRoomAsync_Denies_WhenTheWorkspaceRefusesTheCaller()
    {
        _mockWorkspaceMeetingPolicy.Setup(p => p.ValidateMeetingCreationAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure("User does not have permission to create meetings.", ErrorCodes.Forbidden));

        var request = new CreateTranslationRoomRequest(
            Guid.NewGuid(), "Standup", null, "INSTANT", 10,
            "vi-VN", new List<string> { "en-US" }, null, null, null);

        var result = await _service.CreateTranslationRoomAsync(request, Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        // Nothing may be persisted — the room must not exist at all.
        _mockRoomRepo.Verify(
            r => r.AddAsync(It.IsAny<TranslationRoom>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateTranslationRoomAsync_FailsClosed_WhenTheWorkspaceCannotBeReached()
    {
        _mockWorkspaceMeetingPolicy.Setup(p => p.ValidateMeetingCreationAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure("Could not verify.", ErrorCodes.ServiceUnavailable));

        var request = new CreateTranslationRoomRequest(
            Guid.NewGuid(), "Standup", null, "INSTANT", 10,
            "vi-VN", new List<string> { "en-US" }, null, null, null);

        var result = await _service.CreateTranslationRoomAsync(request, Guid.NewGuid());

        // An unreachable WorkspaceService must not become a way around the permission check.
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ServiceUnavailable);
        _mockRoomRepo.Verify(
            r => r.AddAsync(It.IsAny<TranslationRoom>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateTranslationRoomAsync_ChecksThePolicyWithTheResolvedLanguages()
    {
        _mockRoomRepo
            .Setup(r => r.ExistsByCodeAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var workspaceId = Guid.NewGuid();
        var hostId = Guid.NewGuid();

        var request = new CreateTranslationRoomRequest(
            workspaceId, "Standup", null, "INSTANT", 10,
            "vi-VN", new List<string> { "en-US", "ja-JP" }, null, null, null);

        var result = await _service.CreateTranslationRoomAsync(request, hostId);

        result.IsSuccess.Should().BeTrue();
        // The workspace vetoes the languages actually being used, so the check has to run after
        // language resolution rather than on the raw request — which is also why these arrive
        // normalized ("en-US" -> "en") rather than as the codes the request carried.
        _mockWorkspaceMeetingPolicy.Verify(p => p.ValidateMeetingCreationAsync(
                workspaceId,
                hostId,
                It.Is<IEnumerable<string>>(langs => langs.SequenceEqual(new[] { "en", "ja" })),
                // WT-466: and the SOURCE language, normalized the same way. It used not to be
                // passed at all, so a workspace whitelist that excluded "vi" still let this room
                // be created — the one language the host actually speaks was the one the policy
                // never saw. Asserting the literal, not It.IsAny, is the point of the test.
                "vi",
                It.IsAny<CancellationToken>()),
            Times.Once);
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

    /// <summary>
    /// This test used to assert the opposite — that a room created with no invitees publishes
    /// NOTHING — and carried no reason for it. That behaviour is the bug: the publish sat inside
    /// the `if (InvitedEmails.Any())` block, so creating a room the ordinary way (no emails typed,
    /// workspace members already see each other's meetings) rang no bell and every other client
    /// had to press F5. It produced two contradictory reports of the same feature on the same
    /// evening, both correct, about two different ways of creating a room.
    ///
    /// Publishing is not a disclosure. The payload only says "this workspace's meeting list
    /// changed"; what any given member may then SEE is decided server-side by
    /// GetTranslationRoomsAsync when they refetch. So there is nothing to protect by staying
    /// silent, and a list that needs a manual refresh to be right is simply wrong.
    /// </summary>
    [Fact]
    public async Task CreateTranslationRoomAsync_Publishes_EvenWhenNobodyIsInvited()
    {
        var workspaceId = Guid.NewGuid();
        _mockRoomRepo
            .Setup(r => r.ExistsByCodeAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var request = new CreateTranslationRoomRequest(
            workspaceId, "Solo", null, "INSTANT", 10,
            "vi-VN", new List<string> { "en-US" }, null, null, null);

        var result = await _service.CreateTranslationRoomAsync(request, Guid.NewGuid());

        result.IsSuccess.Should().BeTrue();
        _mockRedisStateRepository.Verify(
            r => r.PublishAsync(
                MeetingEventsChannel,
                It.Is<string>(payload =>
                    payload.Contains("\"eventType\":\"MeetingInvited\"")
                    && payload.Contains($"\"workspaceId\":\"{workspaceId}\""))),
            Times.Once);
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
        // Already terminal, so nothing changed and there is nothing new to announce.
        _mockAudioRouteEventProcessor.Verify(
            a => a.ProcessEventAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string>(), default),
            Times.Never);
    }

    // ---------------------------------------------------------------------------------
    // WT-314 — every terminal room transition must reach the AI pipeline.
    //
    // livekit_ingress_worker's "AIBot_{room}" is summoned by MeetingRoomService on every
    // JoinMeetingAsync and released only by an AUDIO_ROUTES_UPDATED carrying a terminal room
    // status, which AudioRouteEventProcessor emits from session_ends. Cancel and Expire never
    // called the processor at all, and both are reachable only from SCHEDULED/WAITING — the
    // states that have no audio routes — so nothing else would have published either. The bot
    // stayed connected, billing LiveKit connection minutes, and kept LiveKit's own
    // empty_timeout from ever collecting the room.
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task CancelTranslationRoomAsync_PublishesSessionEnds_SoTheIngressBotIsReleased()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = new TranslationRoom
        {
            Id = roomId,
            HostId = hostId,
            Status = "WAITING",
            Settings = "{\"requires_approval\":true}"
        };

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, It.IsAny<CancellationToken>())).ReturnsAsync(room);

        var result = await _service.CancelTranslationRoomAsync(roomId, hostId);

        result.IsSuccess.Should().BeTrue();
        room.Status.Should().Be("CANCELLED");
        _mockAudioRouteEventProcessor.Verify(
            a => a.ProcessEventAsync(roomId, null, AudioRoutingEventType.session_ends.ToString(), "{}", default),
            Times.Once);
    }

    [Fact]
    public async Task ExpireTranslationRoomAsync_PublishesSessionEnds_SoTheIngressBotIsReleased()
    {
        var roomId = Guid.NewGuid();
        var room = new TranslationRoom
        {
            Id = roomId,
            HostId = Guid.NewGuid(),
            Status = "WAITING",
            Settings = "{\"requires_approval\":true}"
        };

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, It.IsAny<CancellationToken>())).ReturnsAsync(room);

        var result = await _service.ExpireTranslationRoomAsync(roomId);

        result.IsSuccess.Should().BeTrue();
        room.Status.Should().Be("EXPIRED");
        _mockAudioRouteEventProcessor.Verify(
            a => a.ProcessEventAsync(roomId, null, AudioRoutingEventType.session_ends.ToString(), "{}", default),
            Times.Once);
    }

    [Fact]
    public async Task CancelTranslationRoomAsync_StillSucceeds_WhenTheLifecyclePublishFails()
    {
        // The room is already persisted as CANCELLED by this point. A dead Redis must not turn
        // a completed cancel into an error for the caller — the ingress worker's own idle sweep
        // is the backstop for the bot.
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = new TranslationRoom
        {
            Id = roomId,
            HostId = hostId,
            Status = "SCHEDULED",
            Settings = "{\"requires_approval\":true}"
        };

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, It.IsAny<CancellationToken>())).ReturnsAsync(room);
        _mockAudioRouteEventProcessor
            .Setup(a => a.ProcessEventAsync(roomId, null, AudioRoutingEventType.session_ends.ToString(), "{}", default))
            .ThrowsAsync(new InvalidOperationException("redis is down"));

        var result = await _service.CancelTranslationRoomAsync(roomId, hostId);

        result.IsSuccess.Should().BeTrue();
        room.Status.Should().Be("CANCELLED");
        _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExpireTranslationRoomAsync_StillSucceeds_WhenTheLifecyclePublishFails()
    {
        var roomId = Guid.NewGuid();
        var room = new TranslationRoom
        {
            Id = roomId,
            HostId = Guid.NewGuid(),
            Status = "SCHEDULED",
            Settings = "{\"requires_approval\":true}"
        };

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, It.IsAny<CancellationToken>())).ReturnsAsync(room);
        _mockAudioRouteEventProcessor
            .Setup(a => a.ProcessEventAsync(roomId, null, AudioRoutingEventType.session_ends.ToString(), "{}", default))
            .ThrowsAsync(new InvalidOperationException("redis is down"));

        var result = await _service.ExpireTranslationRoomAsync(roomId);

        result.IsSuccess.Should().BeTrue();
        room.Status.Should().Be("EXPIRED");
        _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
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
    public async Task EndTranslationRoomAsync_ShouldDisconnectOnlyConnectedParticipants()
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
        // WT-563: a WAITING row is left alone. DISCONNECTED is the state the rejoin path reads as
        // proof the host admitted somebody, so writing it for the lobby's occupants said they had
        // been in a room they were never let into. Ending the room makes their knock moot rather
        // than granting it.
        participant2.Status.Should().Be("WAITING");
        participant3.Status.Should().Be("INVITED"); // unchanged
        // One write, for the one participant who was actually in the room.
        _mockParticipantRepo.Verify(p => p.Update(It.IsAny<TranslationRoomParticipant>()), Times.Exactly(1));
    }

    // ------------------------------------------------------------------
    // S7 — a participant who joins mid-meeting must get audio route rows.
    //
    // Routes were generated exactly once, inside StartTranslationRoomAsync; the comment there
    // claimed more were generated "as more participants join" and no code path did that. With
    // no route row, BaseWorker.is_voice_clone_consented fails closed and the late joiner is
    // permanently dubbed in a hashed default voice instead of their own cloned one.
    // ------------------------------------------------------------------

    private TranslationRoom ArrangeJoinableRoom(string status, string roomCode = "abc-defg-hij")
    {
        var room = new TranslationRoom
        {
            Id = Guid.NewGuid(),
            HostId = Guid.NewGuid(),
            TranslationRoomCode = roomCode,
            Status = status,
            TranslationRoomType = "INSTANT",
            SourceLanguage = "en",
            TargetLanguages = "[\"vi\"]",
            Settings = "{\"requires_approval\":false,\"history_access\":\"HostOnly\"}"
        };

        _mockRoomRepo.Setup(r => r.GetByCodeAsync(roomCode, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        _mockAudioRouteService.Setup(s => s.AddRoutesForParticipantAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new List<TranslationRoomAudioRouteDto>()));
        return room;
    }

    [Fact]
    public async Task JoinTranslationRoomAsync_AddsAudioRoutes_WhenTheRoomIsAlreadyRunning()
    {
        var room = ArrangeJoinableRoom("IN_PROGRESS");
        var userId = Guid.NewGuid();
        _mockParticipantRepo.Setup(p => p.GetByRoomAndUserAsync(room.Id, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomParticipant?)null);

        var result = await _service.JoinTranslationRoomAsync(
            new JoinTranslationRoomRequest(room.TranslationRoomCode, "Late Joiner", "vi", "vi"), userId);

        result.IsSuccess.Should().BeTrue(result.Error);
        _mockAudioRouteService.Verify(
            s => s.AddRoutesForParticipantAsync(room.Id, It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task JoinTranslationRoomAsync_DoesNotAddRoutes_BeforeTheRoomStarts()
    {
        // StartTranslationRoomAsync generates the whole mesh for the roster it finds, so doing
        // it here too would charge every pre-start join for work Start is about to redo.
        var room = ArrangeJoinableRoom("WAITING");
        var userId = Guid.NewGuid();
        _mockParticipantRepo.Setup(p => p.GetByRoomAndUserAsync(room.Id, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomParticipant?)null);

        await _service.JoinTranslationRoomAsync(
            new JoinTranslationRoomRequest(room.TranslationRoomCode, "Early Bird", "vi", "vi"), userId);

        _mockAudioRouteService.Verify(
            s => s.AddRoutesForParticipantAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task JoinTranslationRoomAsync_StillSucceeds_WhenRouteGenerationFails()
    {
        // Best-effort, exactly like the Start-path call it complements: the participant is
        // already saved, and this only decides which voice they are dubbed in.
        var room = ArrangeJoinableRoom("IN_PROGRESS");
        var userId = Guid.NewGuid();
        _mockParticipantRepo.Setup(p => p.GetByRoomAndUserAsync(room.Id, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomParticipant?)null);
        _mockAudioRouteService.Setup(s => s.AddRoutesForParticipantAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<List<TranslationRoomAudioRouteDto>>("boom", ErrorCodes.InternalServerError));

        var result = await _service.JoinTranslationRoomAsync(
            new JoinTranslationRoomRequest(room.TranslationRoomCode, "Late Joiner", "vi", "vi"), userId);

        result.IsSuccess.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task StartTranslationRoomAsync_ReconcilesRoutes_WhenTheRoomIsAlreadyInProgress()
    {
        // "Just restart it" used to be no repair at all: this early return skipped route
        // generation entirely, so a late joiner with no route row stayed stuck.
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(new TranslationRoom
        {
            Id = roomId,
            HostId = hostId,
            Status = "IN_PROGRESS",
            TranslationRoomType = "INSTANT",
            SourceLanguage = "en",
            TargetLanguages = "[\"vi\"]",
            Settings = "{\"requires_approval\":false}"
        });

        var result = await _service.StartTranslationRoomAsync(roomId, hostId, null);

        result.IsSuccess.Should().BeTrue(result.Error);
        _mockAudioRouteService.Verify(s => s.GenerateRoutesAsync(roomId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartTranslationRoomAsync_AlreadyInProgressWithoutAnActiveSession_DoesNotBroadcastRoutes()
    {
        // WT-339: an idempotent re-Start of an OPEN room must keep respecting the same split as
        // the first Start. IN_PROGRESS can now mean "room open, translation not yet started", so
        // the repair path still only configures routes unless a numbered TranslationSession exists.
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(new TranslationRoom
        {
            Id = roomId,
            HostId = hostId,
            Status = "IN_PROGRESS",
            TranslationRoomType = "INSTANT",
            SourceLanguage = "en",
            TargetLanguages = "[\"vi\"]",
            Settings = "{\"requires_approval\":false}"
        });
        _mockSessionRepo.Setup(s => s.GetActiveSessionByRoomIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomSession?)null);

        var emitted = new List<string>();
        _mockAudioRouteEventProcessor
            .Setup(a => a.ProcessEventAsync(roomId, null, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid?, string, string, CancellationToken>((_, _, eventType, _, _) => emitted.Add(eventType))
            .ReturnsAsync(Result.Success());

        var result = await _service.StartTranslationRoomAsync(roomId, hostId, null);

        result.IsSuccess.Should().BeTrue(result.Error);
        emitted.Should().Equal(AudioRoutingEventType.config_ready.ToString());
    }

    [Fact]
    public async Task StartTranslationRoomAsync_ConfiguresRoutesButDoesNotBroadcastThem()
    {
        // S8. GenerateRoutesAsync creates every route at PENDING, and the ONLY transition out of
        // PENDING is config_ready — which nothing in this repository ever emitted. session_starts
        // is only accepted from READY, so it was rejected on every freshly generated route and
        // the route sat at PENDING (rendered as "Waiting") for the whole meeting, spoken in or
        // not. Start is the point at which routes really are configured, so it is the producer.
        //
        // WT-339: and it is the producer of config_ready ONLY. This assertion used to require
        // session_starts here too, which is the inversion that matters: emitting it made opening
        // a room start translation — routes went BROADCASTING, AUDIO_ROUTES_UPDATED went out, and
        // livekit_ingress_worker began transcribing a room whose host had not pressed anything.
        // Opening a room leaves the routes READY and waiting.
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(new TranslationRoom
        {
            Id = roomId,
            HostId = hostId,
            Status = "WAITING",
            TranslationRoomType = "INSTANT",
            SourceLanguage = "en",
            TargetLanguages = "[\"vi\"]",
            Settings = "{\"requires_approval\":false}"
        });
        _mockAudioRouteRepo.Setup(r => r.GetRoutesByRoomIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomAudioRoute>());

        var emitted = new List<string>();
        _mockAudioRouteEventProcessor
            .Setup(a => a.ProcessEventAsync(roomId, null, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid?, string, string, CancellationToken>((_, _, eventType, _, _) => emitted.Add(eventType))
            .ReturnsAsync(Result.Success());

        var result = await _service.StartTranslationRoomAsync(roomId, hostId, null);

        result.IsSuccess.Should().BeTrue(result.Error);
        emitted.Should().Equal(AudioRoutingEventType.config_ready.ToString());
    }

    [Fact]
    public async Task StartTranslationRoomAsync_DoesNotOpenATranslationSession()
    {
        // WT-339, the other half of the same rule and the reason the routes have nothing to
        // broadcast on: opening a room puts a LiveKit call on the air and stops there. The
        // numbered TranslationSession — what the transcript labels "Translation 1" — belongs to
        // the host's Start Translation press, not to the room being opened.
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(new TranslationRoom
        {
            Id = roomId,
            HostId = hostId,
            Status = "WAITING",
            TranslationRoomType = "INSTANT",
            SourceLanguage = "en",
            TargetLanguages = "[\"vi\"]",
            Settings = "{\"requires_approval\":false}"
        });

        var result = await _service.StartTranslationRoomAsync(roomId, hostId, null);

        result.IsSuccess.Should().BeTrue(result.Error);
        _mockSessionRepo.Verify(
            s => s.AddAsync(It.IsAny<TranslationRoomSession>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ResumeTranslationRoomAsync_OpensASessionAndTakesTheRoutesToBroadcasting()
    {
        // WT-339: pressing Start Translation on an OPEN room. The room is already IN_PROGRESS and
        // its routes have been sitting at READY since it was opened, so room_resume (a PAUSED-only
        // transition) cannot move them — the readiness pair is what does, and it may only do so
        // because this method opened a session first. Both are still sent: whichever one is not
        // the applicable transition is a no-op.
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = NewStartableRoom(roomId, hostId);
        room.Status = "IN_PROGRESS";

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(room);
        _mockSessionRepo.SetupSequence(s => s.GetActiveSessionByRoomIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomSession?)null)
            .ReturnsAsync(new TranslationRoomSession { Id = Guid.NewGuid(), TranslationRoomId = roomId });

        var emitted = new List<string>();
        _mockAudioRouteEventProcessor
            .Setup(a => a.ProcessEventAsync(roomId, null, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid?, string, string, CancellationToken>((_, _, eventType, _, _) => emitted.Add(eventType))
            .ReturnsAsync(Result.Success());

        var result = await _service.ResumeTranslationRoomAsync(roomId, hostId);

        result.IsSuccess.Should().BeTrue(result.Error);
        _mockSessionRepo.Verify(
            s => s.AcquireSessionStartLockAsync(roomId, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockSessionRepo.Verify(
            s => s.AddAsync(It.IsAny<TranslationRoomSession>(), It.IsAny<CancellationToken>()),
            Times.Once);
        emitted.Should().Equal(
            AudioRoutingEventType.config_ready.ToString(),
            AudioRoutingEventType.session_starts.ToString(),
            AudioRoutingEventType.room_resume.ToString());
    }

    [Fact]
    public async Task ResumeTranslationRoomAsync_AlreadyHasActiveSession_DoesNotOpenDuplicateSession()
    {
        // WT-339: accepting IN_PROGRESS makes the Start Translation endpoint retry-safe for the
        // newly split room-open/translation-start lifecycle. It must not create a second active
        // TranslationSession when the host double-clicks or the client retries.
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = NewStartableRoom(roomId, hostId);
        room.Status = "IN_PROGRESS";

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(room);
        _mockSessionRepo.Setup(s => s.GetActiveSessionByRoomIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationRoomSession { Id = Guid.NewGuid(), TranslationRoomId = roomId });

        var result = await _service.ResumeTranslationRoomAsync(roomId, hostId);

        result.IsSuccess.Should().BeTrue(result.Error);
        _mockSessionRepo.Verify(
            s => s.AcquireSessionStartLockAsync(roomId, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockSessionRepo.Verify(
            s => s.AddAsync(It.IsAny<TranslationRoomSession>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task JoinTranslationRoomAsync_MarksTheNewRoutesReady_WhenTranslationIsAlreadyRunning()
    {
        // A late joiner's routes are created PENDING in a room that is already broadcasting, so
        // they need the same readiness pair or they render as "Waiting" for that participant.
        var room = ArrangeJoinableRoom("IN_PROGRESS", "xyz-defg-hij");
        var userId = Guid.NewGuid();
        _mockParticipantRepo.Setup(p => p.GetByRoomAndUserAsync(room.Id, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomParticipant?)null);
        _mockSessionRepo.Setup(s => s.GetActiveSessionByRoomIdAsync(room.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationRoomSession { Id = Guid.NewGuid(), TranslationRoomId = room.Id });

        var emitted = new List<string>();
        _mockAudioRouteEventProcessor
            .Setup(a => a.ProcessEventAsync(room.Id, null, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid?, string, string, CancellationToken>((_, _, eventType, _, _) => emitted.Add(eventType))
            .ReturnsAsync(Result.Success());

        await _service.JoinTranslationRoomAsync(
            new JoinTranslationRoomRequest(room.TranslationRoomCode, "Late Joiner", "vi", "vi"), userId);

        emitted.Should().Equal(
            AudioRoutingEventType.config_ready.ToString(),
            AudioRoutingEventType.session_starts.ToString());
    }

    [Fact]
    public async Task JoinTranslationRoomAsync_DoesNotBroadcast_WhenTranslationHasNotStarted()
    {
        // WT-339: the room being open is not translation being on. Somebody who joins between
        // "host opened the room" and "host pressed Start Translation" gets configured routes that
        // wait with everyone else's — this path used to switch the whole room's routes to
        // BROADCASTING on their behalf, so a guest arriving early started the AI.
        var room = ArrangeJoinableRoom("IN_PROGRESS", "xyz-defg-hik");
        var userId = Guid.NewGuid();
        _mockParticipantRepo.Setup(p => p.GetByRoomAndUserAsync(room.Id, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomParticipant?)null);
        _mockSessionRepo.Setup(s => s.GetActiveSessionByRoomIdAsync(room.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomSession?)null);

        var emitted = new List<string>();
        _mockAudioRouteEventProcessor
            .Setup(a => a.ProcessEventAsync(room.Id, null, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid?, string, string, CancellationToken>((_, _, eventType, _, _) => emitted.Add(eventType))
            .ReturnsAsync(Result.Success());

        await _service.JoinTranslationRoomAsync(
            new JoinTranslationRoomRequest(room.TranslationRoomCode, "Early Joiner", "vi", "vi"), userId);

        emitted.Should().Equal(AudioRoutingEventType.config_ready.ToString());
    }

    // ── Accepting an invitation ────────────────────────────────────────────────────────────────
    //
    // An invitation existed as an email and a PENDING row and nothing in the app could answer it.
    // Accept is the invitee's RSVP and is deliberately NOT a join: the meeting is usually still
    // ahead of them, so joining would put them in a room that has not opened.

    private Mock<ITranslationRoomInvitationRepository> ArrangeInvitationLookup(
        TranslationRoomInvitation? found)
    {
        var repo = new Mock<ITranslationRoomInvitationRepository>();
        repo.Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<TranslationRoomInvitation, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(found);
        _mockUow.Setup(u => u.TranslationRoomInvitationRepository).Returns(repo.Object);
        return repo;
    }

    [Fact]
    public async Task AcceptTranslationRoomInvitationAsync_FlipsPendingToAccepted()
    {
        var roomId = Guid.NewGuid();
        var invitation = new TranslationRoomInvitation
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = roomId,
            Email = "invitee@example.com",
            Status = "PENDING"
        };
        var repo = ArrangeInvitationLookup(invitation);

        var result = await _service.AcceptTranslationRoomInvitationAsync(
            roomId, Guid.NewGuid(), "invitee@example.com");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be("ACCEPTED");
        invitation.Status.Should().Be("ACCEPTED");
        repo.Verify(r => r.Update(invitation), Times.Once);
        _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AcceptTranslationRoomInvitationAsync_IsIdempotent()
    {
        // The same notification carries an Accept button in two places — the popup and the bell —
        // so being clicked twice is ordinary use, not an error to report back to the invitee.
        var roomId = Guid.NewGuid();
        ArrangeInvitationLookup(new TranslationRoomInvitation
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = roomId,
            Email = "invitee@example.com",
            Status = "ACCEPTED"
        });

        var result = await _service.AcceptTranslationRoomInvitationAsync(
            roomId, Guid.NewGuid(), "invitee@example.com");

        result.IsSuccess.Should().BeTrue();
        _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AcceptTranslationRoomInvitationAsync_RefusesWhenNoInvitationIsAddressedToTheCaller()
    {
        ArrangeInvitationLookup(null);

        var result = await _service.AcceptTranslationRoomInvitationAsync(
            Guid.NewGuid(), Guid.NewGuid(), "stranger@example.com");

        // NotFound rather than Forbidden: a caller with no invitation must not learn from this
        // endpoint whether a room with that id exists.
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
        _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AcceptTranslationRoomInvitationAsync_RefusesWithoutAnEmailClaim()
    {
        // Invitations are keyed by ADDRESS. With no email there is nothing to match, and probing
        // the repository with an empty string would match nothing only by luck.
        var repo = ArrangeInvitationLookup(null);

        var result = await _service.AcceptTranslationRoomInvitationAsync(
            Guid.NewGuid(), Guid.NewGuid(), "   ");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
        repo.Verify(r => r.FirstOrDefaultAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<TranslationRoomInvitation, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AcceptTranslationRoomInvitationAsync_WillNotReverseADecline()
    {
        var roomId = Guid.NewGuid();
        ArrangeInvitationLookup(new TranslationRoomInvitation
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = roomId,
            Email = "invitee@example.com",
            Status = "DECLINED"
        });

        var result = await _service.AcceptTranslationRoomInvitationAsync(
            roomId, Guid.NewGuid(), "invitee@example.com");

        // Nothing writes DECLINED today. This is the guard for when something does: the host has
        // already been told this person is not coming.
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidState);
        _mockUow.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
