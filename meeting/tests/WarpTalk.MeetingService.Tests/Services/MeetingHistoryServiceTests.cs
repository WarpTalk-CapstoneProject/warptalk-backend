using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.MeetingService.Application.Services;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Enums;
using WarpTalk.MeetingService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.MeetingService.Tests.Services;

public class MeetingHistoryServiceTests
{
    private readonly Mock<IUnitOfWork> _unitOfWorkMock;
    private readonly Mock<IMeetingRoomRepository> _roomRepoMock;
    private readonly Mock<IMeetingParticipantRepository> _participantRepoMock;
    private readonly Mock<IMeetingChatMessageRepository> _chatMessageRepoMock;
    private readonly MeetingHistoryService _sut;

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _roomId = Guid.NewGuid();

    public MeetingHistoryServiceTests()
    {
        _unitOfWorkMock = new Mock<IUnitOfWork>();
        _roomRepoMock = new Mock<IMeetingRoomRepository>();
        _participantRepoMock = new Mock<IMeetingParticipantRepository>();
        _chatMessageRepoMock = new Mock<IMeetingChatMessageRepository>();

        _unitOfWorkMock.Setup(u => u.MeetingRoomRepository).Returns(_roomRepoMock.Object);
        _unitOfWorkMock.Setup(u => u.MeetingParticipantRepository).Returns(_participantRepoMock.Object);
        _unitOfWorkMock.Setup(u => u.MeetingChatMessageRepository).Returns(_chatMessageRepoMock.Object);

        _sut = new MeetingHistoryService(_unitOfWorkMock.Object);
    }

    private MeetingRoom CreateRoom(Guid? id = null, Guid? createdBy = null) => new()
    {
        Id = id ?? _roomId,
        TranslationRoomId = Guid.NewGuid(),
        ProviderRoomName = "test-room",
        Status = MeetingStatus.Finished.ToString(),
        IsActive = true,
        CreatedBy = createdBy ?? _userId,
        CreatedAt = DateTime.UtcNow.AddHours(-1),
        EndedAt = DateTime.UtcNow,
        MeetingParticipants = new List<MeetingParticipant>()
    };

    // --- GetMeetingRoomDetail Tests ---

    [Fact]
    public async Task GetMeetingRoomDetailAsync_RoomNotFound_ReturnsFailure()
    {
        _roomRepoMock.Setup(r => r.GetByIdAsync(_roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((MeetingRoom?)null);

        var result = await _sut.GetMeetingRoomDetailAsync(_roomId, _userId);

        Assert.False(result.IsSuccess);
        Assert.Equal("NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public async Task GetMeetingRoomDetailAsync_NotAuthorized_ReturnsFailure()
    {
        var otherUserId = Guid.NewGuid();
        _roomRepoMock.Setup(r => r.GetByIdAsync(_roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRoom(createdBy: Guid.NewGuid()));

        _participantRepoMock.Setup(p => p.AnyAsync(
                It.IsAny<Expression<Func<MeetingParticipant, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _sut.GetMeetingRoomDetailAsync(_roomId, otherUserId);

        Assert.False(result.IsSuccess);
        Assert.Equal("FORBIDDEN", result.ErrorCode);
    }

    [Fact]
    public async Task GetMeetingRoomDetailAsync_Host_ReturnsSuccess()
    {
        var room = CreateRoom(createdBy: _userId);
        _roomRepoMock.Setup(r => r.GetByIdAsync(_roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        _participantRepoMock.Setup(p => p.FindAsync(
                It.IsAny<Expression<Func<MeetingParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MeetingParticipant>());

        // Mock Query() for chat count
        var emptyQueryable = new List<MeetingChatMessage>().AsQueryable();
        _chatMessageRepoMock.Setup(r => r.Query()).Returns(emptyQueryable);

        var result = await _sut.GetMeetingRoomDetailAsync(_roomId, _userId);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.True(result.Value!.Room.IsHost);
    }

    [Fact]
    public async Task GetMeetingRoomDetailAsync_Participant_ReturnsSuccess()
    {
        var otherHost = Guid.NewGuid();
        var room = CreateRoom(createdBy: otherHost);

        _roomRepoMock.Setup(r => r.GetByIdAsync(_roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        _participantRepoMock.Setup(p => p.AnyAsync(
                It.IsAny<Expression<Func<MeetingParticipant, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var participants = new List<MeetingParticipant>
        {
            new() { Id = Guid.NewGuid(), MeetingRoomId = _roomId, UserId = _userId, ProviderIdentity = "test", IsActive = true }
        };

        _participantRepoMock.Setup(p => p.FindAsync(
                It.IsAny<Expression<Func<MeetingParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(participants);

        var emptyQueryable = new List<MeetingChatMessage>().AsQueryable();
        _chatMessageRepoMock.Setup(r => r.Query()).Returns(emptyQueryable);

        var result = await _sut.GetMeetingRoomDetailAsync(_roomId, _userId);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.Room.IsHost);
        Assert.Single(result.Value.Participants);
    }
}
