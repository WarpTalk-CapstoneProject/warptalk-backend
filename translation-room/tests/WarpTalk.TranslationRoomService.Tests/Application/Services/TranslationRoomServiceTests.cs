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
    private readonly Mock<ILanguagePolicy> _mockLanguagePolicy;
    private readonly Mock<IAudioRouteEventProcessorService> _mockAudioRouteEventProcessor;
    private readonly Mock<Microsoft.Extensions.Logging.ILogger<WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService>> _mockLogger;
    private readonly WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService _service;

    public TranslationRoomServiceTests()
    {
        _mockUow = new Mock<IUnitOfWork>();
        _mockRoomRepo = new Mock<ITranslationRoomRepository>();
        _mockParticipantRepo = new Mock<ITranslationRoomParticipantRepository>();
        _mockLanguagePolicy = new Mock<ILanguagePolicy>();
        _mockAudioRouteEventProcessor = new Mock<IAudioRouteEventProcessorService>();
        _mockLogger = new Mock<Microsoft.Extensions.Logging.ILogger<WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService>>();

        _mockUow.Setup(u => u.TranslationRoomRepository).Returns(_mockRoomRepo.Object);
        _mockUow.Setup(u => u.TranslationRoomParticipantRepository).Returns(_mockParticipantRepo.Object);

        _mockLanguagePolicy.Setup(v => v.IsSupportedAsync(It.IsAny<string>())).ReturnsAsync(true);
        _mockLanguagePolicy.Setup(v => v.ValidateParticipantLanguagesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TranslationRoom>())).ReturnsAsync((string?)null);

        _service = new WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService(_mockUow.Object, _mockLanguagePolicy.Object, _mockAudioRouteEventProcessor.Object, _mockLogger.Object);
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
            Status = RoomStatus.WAITING,
            TranslationRoomType = TranslationRoomType.INSTANT.ToString()
        };

        var request = new JoinTranslationRoomRequest(roomCode, "Host User", "en", "vi");

        _mockRoomRepo.Setup(r => r.GetByCodeAsync(roomCode, It.IsAny<IEnumerable<RoomStatus>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        _mockParticipantRepo.Setup(p => p.GetByRoomAndUserAsync(room.Id, hostId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomParticipant?)null);

        // Act
        var result = await _service.JoinTranslationRoomAsync(request, hostId);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.Participant.Role.Should().Be(TranslationRoomParticipantRole.HOST);
        _mockParticipantRepo.Verify(p => p.AddAsync(It.Is<TranslationRoomParticipant>(pt => pt.Role == TranslationRoomParticipantRole.HOST.ToString()), It.IsAny<CancellationToken>()), Times.Once);
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
            Status = RoomStatus.ENDED,
            TranslationRoomType = TranslationRoomType.INSTANT.ToString()
        };

        var request = new JoinTranslationRoomRequest(roomCode, "User", "en", "vi");

        _mockRoomRepo.Setup(r => r.GetByCodeAsync(roomCode, It.IsAny<IEnumerable<RoomStatus>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoom?)null);

        // Act
        var result = await _service.JoinTranslationRoomAsync(request, userId);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(TranslationRoomConstants.ErrorRoomNotFound);
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
            Status = RoomStatus.WAITING,
            HostId = Guid.NewGuid(),
            TranslationRoomType = TranslationRoomType.INSTANT.ToString(),
            Settings = "{\"requires_approval\":false}"
        };

        var existingParticipant = new TranslationRoomParticipant
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = roomId,
            UserId = userId,
            DisplayName = "Old Name",
            Role = TranslationRoomParticipantRole.PARTICIPANT.ToString(),
            Status = TranslationRoomParticipantStatus.INVITED
        };

        var request = new JoinTranslationRoomRequest(roomCode, "New Name", "fr", "es");

        _mockRoomRepo.Setup(r => r.GetByCodeAsync(roomCode, It.IsAny<IEnumerable<RoomStatus>>(), It.IsAny<CancellationToken>()))
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
        existingParticipant.Status.Should().Be(TranslationRoomParticipantStatus.CONNECTED);
        _mockParticipantRepo.Verify(p => p.Update(existingParticipant), Times.Once);
    }

    [Fact]
    public async Task StartTranslationRoomAsync_ValidState_UpdatesStatusAndFiresEvent()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = new TranslationRoom { Id = roomId, HostId = hostId, Status = RoomStatus.WAITING };

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(room);

        var result = await _service.StartTranslationRoomAsync(roomId, hostId);

        result.IsSuccess.Should().BeTrue();
        room.Status.Should().Be(RoomStatus.IN_PROGRESS);
        room.StartedAt.Should().NotBeNull();
        _mockAudioRouteEventProcessor.Verify(a => a.ProcessEventAsync(roomId, null, AudioRoutingEventType.session_starts.ToString(), "{}", default), Times.Once);
    }

    [Fact]
    public async Task StartTranslationRoomAsync_InvalidState_ReturnsError()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = new TranslationRoom { Id = roomId, HostId = hostId, Status = RoomStatus.SCHEDULED };

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(room);

        var result = await _service.StartTranslationRoomAsync(roomId, hostId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(TranslationRoomConstants.ErrorInvalidTransitionToInProgress);
        _mockAudioRouteEventProcessor.Verify(a => a.ProcessEventAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task EndTranslationRoomAsync_CalculatesDurationAndFiresEvent()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var startedAt = DateTime.UtcNow.AddMinutes(-30);
        var room = new TranslationRoom { Id = roomId, HostId = hostId, Status = RoomStatus.IN_PROGRESS, StartedAt = startedAt };

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(room);

        var result = await _service.EndTranslationRoomAsync(roomId, hostId);

        result.IsSuccess.Should().BeTrue();
        room.Status.Should().Be(RoomStatus.ENDED);
        room.EndedAt.Should().NotBeNull();
        room.DurationSeconds.Should().BeGreaterOrEqualTo(1800); // 30 mins = 1800s
        _mockAudioRouteEventProcessor.Verify(a => a.ProcessEventAsync(roomId, null, AudioRoutingEventType.session_ends.ToString(), "{}", default), Times.Once);
    }

    [Fact]
    public async Task ExpireTranslationRoomAsync_Idempotent_ReturnsSuccess()
    {
        var roomId = Guid.NewGuid();
        var room = new TranslationRoom { Id = roomId, Status = RoomStatus.EXPIRED };

        _mockRoomRepo.Setup(r => r.GetByIdAsync(roomId, default)).ReturnsAsync(room);

        var result = await _service.ExpireTranslationRoomAsync(roomId);

        result.IsSuccess.Should().BeTrue();
        _mockRoomRepo.Verify(r => r.Update(It.IsAny<TranslationRoom>()), Times.Never);
    }
}
