using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

public class ParticipantManagementServiceTests
{
    private readonly Mock<ITranslationRoomRepository> _roomRepositoryMock;
    private readonly Mock<ITranslationRoomParticipantRepository> _participantRepositoryMock;
    private readonly Mock<IUnitOfWork> _unitOfWorkMock;
    private readonly Mock<ILogger<TranslationRoomParticipantService>> _loggerMock;
    private readonly TranslationRoomParticipantService _sut;

    public ParticipantManagementServiceTests()
    {
        _roomRepositoryMock = new Mock<ITranslationRoomRepository>();
        _participantRepositoryMock = new Mock<ITranslationRoomParticipantRepository>();
        _unitOfWorkMock = new Mock<IUnitOfWork>();
        _loggerMock = new Mock<ILogger<TranslationRoomParticipantService>>();

        _unitOfWorkMock.Setup(uow => uow.TranslationRoomRepository).Returns(_roomRepositoryMock.Object);
        _unitOfWorkMock.Setup(uow => uow.TranslationRoomParticipantRepository).Returns(_participantRepositoryMock.Object);

        _sut = new TranslationRoomParticipantService(
            _unitOfWorkMock.Object,
            _loggerMock.Object
        );
    }

    [Fact]
    public async Task GetParticipantsAsync_ShouldReturnForbidden_WhenRequesterIsNotInRoom()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();

        var room = new TranslationRoom { Id = roomId, HostId = hostId };
        
        _roomRepositoryMock.Setup(repo => repo.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        _participantRepositoryMock.Setup(repo => repo.GetByRoomAndUserAsync(roomId, requesterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomParticipant?)null);

        var result = await _sut.GetParticipantsAsync(roomId, new GetParticipantsRequest(), requesterId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task UpdateParticipantAudioAsync_ShouldReturnForbidden_WhenRequesterIsNotHost()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();
        var participantId = Guid.NewGuid();

        var room = new TranslationRoom { Id = roomId, HostId = hostId };
        
        _roomRepositoryMock.Setup(repo => repo.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        var result = await _sut.UpdateParticipantAudioAsync(roomId, participantId, new UpdateParticipantAudioRequest(true), requesterId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task KickParticipantAsync_ShouldSetStatusToKicked_WhenRequesterIsHost()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var targetParticipantId = Guid.NewGuid();

        var room = new TranslationRoom { Id = roomId, HostId = hostId };
        var participant = new TranslationRoomParticipant 
        { 
            Id = targetParticipantId,
            TranslationRoomId = roomId,
            Status = nameof(TranslationRoomParticipantStatus.CONNECTED),
            Role = TranslationRoomParticipantRole.PARTICIPANT.ToString()
        };

        _roomRepositoryMock.Setup(repo => repo.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        _participantRepositoryMock.Setup(repo => repo.GetByIdAsync(targetParticipantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(participant);

        var result = await _sut.KickParticipantAsync(roomId, targetParticipantId, hostId);

        result.IsSuccess.Should().BeTrue();
        participant.Status.Should().Be(nameof(TranslationRoomParticipantStatus.KICKED));
        _participantRepositoryMock.Verify(repo => repo.Update(participant), Times.Once);
        _unitOfWorkMock.Verify(uow => uow.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LeaveRoomAsync_ShouldSetStatusToLeft_WhenParticipantLeaves()
    {
        var roomId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var participant = new TranslationRoomParticipant 
        { 
            Id = participantId, 
            UserId = userId,
            TranslationRoomId = roomId,
            Status = nameof(TranslationRoomParticipantStatus.CONNECTED)
        };

        _participantRepositoryMock.Setup(repo => repo.GetByRoomAndUserAsync(roomId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(participant);

        var result = await _sut.LeaveRoomAsync(roomId, userId);

        result.IsSuccess.Should().BeTrue();
        participant.Status.Should().Be(nameof(TranslationRoomParticipantStatus.LEFT));
        _participantRepositoryMock.Verify(repo => repo.Update(participant), Times.Once);
        _unitOfWorkMock.Verify(uow => uow.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
    [Fact]
    public async Task GetParticipantsAsync_ShouldFilterAndSortParticipants()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        
        var room = new TranslationRoom { Id = roomId, HostId = hostId };
        
        var participants = new List<TranslationRoomParticipant>
        {
            new TranslationRoomParticipant { Id = Guid.NewGuid(), TranslationRoomId = roomId, DisplayName = "Alice", Status = nameof(TranslationRoomParticipantStatus.CONNECTED), Role = TranslationRoomParticipantRole.PARTICIPANT.ToString(), JoinedAt = DateTime.UtcNow.AddMinutes(-5), ListenLanguage = "en", SpeakLanguage = "en" },
            new TranslationRoomParticipant { Id = Guid.NewGuid(), TranslationRoomId = roomId, DisplayName = "Bob", Status = nameof(TranslationRoomParticipantStatus.LEFT), Role = TranslationRoomParticipantRole.PARTICIPANT.ToString(), JoinedAt = DateTime.UtcNow.AddMinutes(-10), ListenLanguage = "vi", SpeakLanguage = "vi" },
            new TranslationRoomParticipant { Id = Guid.NewGuid(), TranslationRoomId = roomId, DisplayName = "Charlie", Status = nameof(TranslationRoomParticipantStatus.CONNECTED), Role = TranslationRoomParticipantRole.PARTICIPANT.ToString(), JoinedAt = DateTime.UtcNow.AddMinutes(-2), ListenLanguage = "fr", SpeakLanguage = "fr" }
        };

        _roomRepositoryMock.Setup(repo => repo.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        _participantRepositoryMock.Setup(repo => repo.GetByRoomAndUserAsync(roomId, hostId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(participants[0]); // Requester is host

        _participantRepositoryMock.Setup(repo => repo.FindAsync(It.IsAny<System.Linq.Expressions.Expression<Func<TranslationRoomParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(participants);

        // Test Filter by Status and SortBy DisplayName Descending
        var request = new GetParticipantsRequest
        {
            Status = "CONNECTED",
            SortBy = "displayname",
            IsDescending = true
        };

        var result = await _sut.GetParticipantsAsync(roomId, request, hostId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value![0].DisplayName.Should().Be("Charlie");
        result.Value![1].DisplayName.Should().Be("Alice");
    }
}
