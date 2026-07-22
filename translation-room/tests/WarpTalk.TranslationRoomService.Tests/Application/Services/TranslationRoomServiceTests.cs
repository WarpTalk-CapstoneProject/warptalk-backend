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
    private readonly Mock<ILanguagePolicy> _mockLanguagePolicy;
    private readonly Mock<IAudioRouteEventProcessor> _mockAudioRouteEventProcessor;
    private readonly Mock<ITranslationRoomAudioRouteService> _mockAudioRouteService;
    private readonly Mock<WarpTalk.Shared.Interfaces.IEmailService> _mockEmailService;
    private readonly Mock<Microsoft.Extensions.Logging.ILogger<WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService>> _mockLogger;
    private readonly WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService _service;

    public TranslationRoomServiceTests()
    {
        _mockUow = new Mock<IUnitOfWork>();
        _mockRoomRepo = new Mock<ITranslationRoomRepository>();
        _mockParticipantRepo = new Mock<ITranslationRoomParticipantRepository>();
        _mockAudioRouteRepo = new Mock<ITranslationRoomAudioRouteRepository>();
        _mockLanguagePolicy = new Mock<ILanguagePolicy>();
        _mockAudioRouteEventProcessor = new Mock<IAudioRouteEventProcessor>();
        _mockAudioRouteService = new Mock<ITranslationRoomAudioRouteService>();
        _mockEmailService = new Mock<WarpTalk.Shared.Interfaces.IEmailService>();
        _mockLogger = new Mock<Microsoft.Extensions.Logging.ILogger<WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService>>();

        _mockUow.Setup(u => u.TranslationRoomRepository).Returns(_mockRoomRepo.Object);
        _mockUow.Setup(u => u.TranslationRoomParticipantRepository).Returns(_mockParticipantRepo.Object);
        _mockUow.Setup(u => u.TranslationRoomAudioRouteRepository).Returns(_mockAudioRouteRepo.Object);

        _mockParticipantRepo.Setup(p => p.GetByRoomIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomParticipant>());

        _mockAudioRouteRepo.Setup(r => r.GetRoutesByRoomIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomAudioRoute> { new TranslationRoomAudioRoute() });

        // Start (re)generates audio routes for the current roster; default to success.
        _mockAudioRouteService.Setup(s => s.GenerateRoutesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new List<TranslationRoomAudioRouteDto>()));

        _mockLanguagePolicy.Setup(v => v.IsSupportedAsync(It.IsAny<string>())).ReturnsAsync(true);
        _mockLanguagePolicy.Setup(v => v.ValidateParticipantLanguagesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TranslationRoom>())).ReturnsAsync((string?)null);

        _service = new WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService(_mockUow.Object, _mockLanguagePolicy.Object, _mockAudioRouteEventProcessor.Object, _mockAudioRouteService.Object, _mockEmailService.Object, _mockLogger.Object);
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
    public async Task EndTranslationRoomAsync_CalculatesDurationAndFiresEvent()
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
        room.DurationSeconds.Should().BeGreaterOrEqualTo(1800); // 30 mins = 1800s
        _mockAudioRouteEventProcessor.Verify(a => a.ProcessEventAsync(roomId, null, AudioRoutingEventType.session_ends.ToString(), "{}", default), Times.Once);
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
