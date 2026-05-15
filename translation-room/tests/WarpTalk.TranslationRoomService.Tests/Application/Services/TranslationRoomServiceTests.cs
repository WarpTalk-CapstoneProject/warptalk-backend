using Moq;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.Shared;
using FluentAssertions;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

public class TranslationRoomServiceTests
{
    private readonly Mock<IUnitOfWork> _mockUow;
    private readonly Mock<ITranslationRoomRepository> _mockRoomRepo;
    private readonly Mock<ITranslationRoomParticipantRepository> _mockParticipantRepo;
    private readonly Mock<Microsoft.Extensions.Logging.ILogger<WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService>> _mockLogger;
    private readonly WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService _service;

    public TranslationRoomServiceTests()
    {
        _mockUow = new Mock<IUnitOfWork>();
        _mockRoomRepo = new Mock<ITranslationRoomRepository>();
        _mockParticipantRepo = new Mock<ITranslationRoomParticipantRepository>();
        _mockLogger = new Mock<Microsoft.Extensions.Logging.ILogger<WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService>>();

        _mockUow.Setup(u => u.TranslationRoomRepository).Returns(_mockRoomRepo.Object);
        _mockUow.Setup(u => u.TranslationRoomParticipantRepository).Returns(_mockParticipantRepo.Object);

        _service = new WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService(_mockUow.Object, _mockLogger.Object);
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
            Status = RoomStatus.WAITING.ToString(),
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
            Status = RoomStatus.ENDED.ToString(),
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
            Status = RoomStatus.WAITING.ToString(),
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
            Status = TranslationRoomParticipantStatus.INVITED.ToString()
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
        existingParticipant.ListenLanguage.Should().Be("fr");
        existingParticipant.Status.Should().Be(TranslationRoomParticipantStatus.CONNECTED.ToString());
        _mockParticipantRepo.Verify(p => p.Update(existingParticipant), Times.Once);
    }
}
