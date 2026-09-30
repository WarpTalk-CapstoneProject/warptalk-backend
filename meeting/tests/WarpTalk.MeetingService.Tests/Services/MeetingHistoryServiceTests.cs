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
    private readonly Mock<IRtcStreamParticipantRepository> _participantRepoMock;
    private readonly Mock<IMeetingChatMessageRepository> _chatMessageRepoMock;
    private readonly MeetingHistoryService _sut;

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _roomId = Guid.NewGuid();

    public MeetingHistoryServiceTests()
    {
        _unitOfWorkMock = new Mock<IUnitOfWork>();
        _roomRepoMock = new Mock<IMeetingRoomRepository>();
        _participantRepoMock = new Mock<IRtcStreamParticipantRepository>();
        _chatMessageRepoMock = new Mock<IMeetingChatMessageRepository>();

        _unitOfWorkMock.Setup(u => u.MeetingRoomRepository).Returns(_roomRepoMock.Object);
        _unitOfWorkMock.Setup(u => u.RtcStreamParticipantRepository).Returns(_participantRepoMock.Object);
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
        RtcStreamParticipants = new List<RtcStreamParticipant>()
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
                It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(), It.IsAny<CancellationToken>()))
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
                It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RtcStreamParticipant>());

        _chatMessageRepoMock.Setup(r => r.FindAsync(
                It.IsAny<Expression<Func<MeetingChatMessage, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MeetingChatMessage>());

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
                It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var participants = new List<RtcStreamParticipant>
        {
            new() { Id = Guid.NewGuid(), MeetingRoomId = _roomId, UserId = _userId, ProviderIdentity = "test", IsActive = true }
        };

        _participantRepoMock.Setup(p => p.FindAsync(
                It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(participants);

        _chatMessageRepoMock.Setup(r => r.FindAsync(
                It.IsAny<Expression<Func<MeetingChatMessage, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MeetingChatMessage>());

        var result = await _sut.GetMeetingRoomDetailAsync(_roomId, _userId);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.Room.IsHost);
        Assert.Single(result.Value.Participants);
    }

    // --- GetMeetingHistory Tests ---

    private static RtcStreamParticipant CreateParticipant(Guid roomId, Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        MeetingRoomId = roomId,
        UserId = userId,
        ProviderIdentity = userId.ToString(),
        IsActive = false,
        JoinedAt = DateTime.UtcNow.AddHours(-1),
        LeftAt = DateTime.UtcNow
    };

    private static MeetingChatMessage CreateMessage(Guid roomId, string text, DateTime createdAt, bool isHidden = false) => new()
    {
        Id = Guid.NewGuid(),
        MeetingRoomId = roomId,
        WorkspaceId = Guid.NewGuid(),
        SenderDisplayName = "Sender",
        SenderType = "user",
        MessageType = "text",
        OriginalLanguage = "en",
        OriginalText = text,
        IsHidden = isHidden,
        CreatedAt = createdAt,
        Mentions = string.Empty
    };

    /// <summary>
    /// Backs the three IQueryable sources GetMeetingHistoryAsync reads with in-memory lists. Participants
    /// are also attached to their room's navigation collection, which the membership filter walks.
    /// </summary>
    private void SetupHistory(IEnumerable<MeetingRoom> rooms, IEnumerable<RtcStreamParticipant>? participants = null, IEnumerable<MeetingChatMessage>? messages = null)
    {
        var roomList = rooms.ToList();
        var participantList = (participants ?? Enumerable.Empty<RtcStreamParticipant>()).ToList();
        var messageList = (messages ?? Enumerable.Empty<MeetingChatMessage>()).ToList();

        foreach (var participant in participantList)
            roomList.FirstOrDefault(r => r.Id == participant.MeetingRoomId)?.RtcStreamParticipants.Add(participant);

        _roomRepoMock.Setup(r => r.Query()).Returns(roomList.AsQueryable());
        _participantRepoMock.Setup(p => p.Query()).Returns(participantList.AsQueryable());
        _chatMessageRepoMock.Setup(m => m.Query()).Returns(messageList.AsQueryable());
    }

    [Fact]
    public async Task GetMeetingHistoryAsync_UserIsCreator_ReturnsRoomWithParticipantsAndChatCount()
    {
        var room = CreateRoom(createdBy: _userId);
        var guestId = Guid.NewGuid();
        SetupHistory(
            new[] { room },
            new[] { CreateParticipant(_roomId, _userId), CreateParticipant(_roomId, guestId) },
            new[] { CreateMessage(_roomId, "hi", DateTime.UtcNow.AddMinutes(-2)), CreateMessage(_roomId, "bye", DateTime.UtcNow.AddMinutes(-1)) });

        var result = await _sut.GetMeetingHistoryAsync(_userId, new GetMeetingHistoryRequest { Page = 1, PageSize = 20 });

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.TotalCount);
        Assert.Equal(1, result.Value.Page);
        Assert.Equal(20, result.Value.PageSize);
        var item = Assert.Single(result.Value.Items);
        Assert.Equal(_roomId, item.Room.Id);
        Assert.True(item.Room.IsHost);
        Assert.Equal(2, item.Room.ParticipantCount);
        Assert.Equal(2, item.Participants.Count);
        Assert.Equal(2, item.Room.ChatMessageCount);
        Assert.Equal(new[] { "hi", "bye" }, item.RecentMessages.Select(m => m.OriginalText));
    }

    [Fact]
    public async Task GetMeetingHistoryAsync_UserIsParticipant_ReturnsRoom()
    {
        var room = CreateRoom(createdBy: Guid.NewGuid());
        SetupHistory(new[] { room }, new[] { CreateParticipant(_roomId, _userId) });

        var result = await _sut.GetMeetingHistoryAsync(_userId, new GetMeetingHistoryRequest());

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal(_roomId, item.Room.Id);
        Assert.False(item.Room.IsHost);
    }

    [Fact]
    public async Task GetMeetingHistoryAsync_OtherUsersRoomWithoutMembership_IsNotReturned()
    {
        var ownRoom = CreateRoom(createdBy: _userId);
        var foreignRoomId = Guid.NewGuid();
        var foreignRoom = CreateRoom(id: foreignRoomId, createdBy: Guid.NewGuid());
        SetupHistory(new[] { ownRoom, foreignRoom }, new[] { CreateParticipant(foreignRoomId, Guid.NewGuid()) });

        var result = await _sut.GetMeetingHistoryAsync(_userId, new GetMeetingHistoryRequest());

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.TotalCount);
        Assert.DoesNotContain(result.Value.Items, i => i.Room.Id == foreignRoomId);
    }

    [Fact]
    public async Task GetMeetingHistoryAsync_PageZeroAndPageSizeAboveMax_ClampsToFirstPageAndFifty()
    {
        var rooms = Enumerable.Range(0, 60)
            .Select(i =>
            {
                var room = CreateRoom(id: Guid.NewGuid(), createdBy: _userId);
                room.EndedAt = DateTime.UtcNow.AddMinutes(-i);
                return room;
            })
            .ToList();
        SetupHistory(rooms);

        var result = await _sut.GetMeetingHistoryAsync(_userId, new GetMeetingHistoryRequest { Page = 0, PageSize = 100 });

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value!.Page);
        Assert.Equal(50, result.Value.PageSize);
        Assert.Equal(60, result.Value.TotalCount);
        Assert.Equal(50, result.Value.Items.Count);
        // First page is the most recently ended rooms.
        Assert.Equal(rooms[0].Id, result.Value.Items[0].Room.Id);
    }

    [Fact]
    public async Task GetMeetingHistoryAsync_StatusFilter_ReturnsOnlyMatchingStatuses()
    {
        var finishedRoom = CreateRoom(id: Guid.NewGuid(), createdBy: _userId);
        finishedRoom.Status = MeetingStatus.Finished.ToString();
        var activeRoom = CreateRoom(id: Guid.NewGuid(), createdBy: _userId);
        activeRoom.Status = MeetingStatus.Active.ToString();
        var createdRoom = CreateRoom(id: Guid.NewGuid(), createdBy: _userId);
        createdRoom.Status = MeetingStatus.Created.ToString();
        SetupHistory(new[] { finishedRoom, activeRoom, createdRoom });

        var result = await _sut.GetMeetingHistoryAsync(_userId, new GetMeetingHistoryRequest { Status = "FINISHED,ACTIVE" });

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.TotalCount);
        Assert.Equal(
            new[] { activeRoom.Id, finishedRoom.Id }.OrderBy(id => id),
            result.Value.Items.Select(i => i.Room.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task GetMeetingHistoryAsync_SearchByProviderRoomName_ReturnsMatchingRoom()
    {
        var matchingRoom = CreateRoom(id: Guid.NewGuid(), createdBy: _userId);
        matchingRoom.ProviderRoomName = "Weekly-Standup-Room";
        var otherRoom = CreateRoom(id: Guid.NewGuid(), createdBy: _userId);
        otherRoom.ProviderRoomName = "design-review";
        SetupHistory(new[] { matchingRoom, otherRoom });

        var result = await _sut.GetMeetingHistoryAsync(_userId, new GetMeetingHistoryRequest { Search = "  standup " });

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal(matchingRoom.Id, item.Room.Id);
    }

    [Fact]
    public async Task GetMeetingHistoryAsync_DateRange_ReturnsOnlyRoomsCreatedInRange()
    {
        var from = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);

        var beforeRoom = CreateRoom(id: Guid.NewGuid(), createdBy: _userId);
        beforeRoom.CreatedAt = from.AddDays(-1);
        var inRangeRoom = CreateRoom(id: Guid.NewGuid(), createdBy: _userId);
        inRangeRoom.CreatedAt = from.AddDays(3);
        var afterRoom = CreateRoom(id: Guid.NewGuid(), createdBy: _userId);
        afterRoom.CreatedAt = to.AddDays(1);
        SetupHistory(new[] { beforeRoom, inRangeRoom, afterRoom });

        var result = await _sut.GetMeetingHistoryAsync(_userId, new GetMeetingHistoryRequest { From = from, To = to });

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal(inRangeRoom.Id, item.Room.Id);
    }

    [Fact]
    public async Task GetMeetingHistoryAsync_RecentMessages_ExcludeHiddenAndKeepLastFiveInChronologicalOrder()
    {
        var room = CreateRoom(createdBy: _userId);
        var start = DateTime.UtcNow.AddHours(-1);
        var messages = Enumerable.Range(1, 7)
            .Select(i => CreateMessage(_roomId, $"m{i}", start.AddMinutes(i)))
            .ToList();
        messages.Add(CreateMessage(_roomId, "hidden", start.AddMinutes(10), isHidden: true));
        SetupHistory(new[] { room }, messages: messages);

        var result = await _sut.GetMeetingHistoryAsync(_userId, new GetMeetingHistoryRequest());

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal(new[] { "m3", "m4", "m5", "m6", "m7" }, item.RecentMessages.Select(m => m.OriginalText));
        // The chat count is taken over all messages, hidden ones included.
        Assert.Equal(8, item.Room.ChatMessageCount);
    }
}
