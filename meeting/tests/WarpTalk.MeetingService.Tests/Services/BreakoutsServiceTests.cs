using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Application.Services;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Tests.TestHelpers;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.MeetingService.Tests.Services;

public class BreakoutsServiceTests
{
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly Mock<ITranslationRoomGrpcService> _grpcServiceMock = new();
    private readonly Mock<IRedisService> _redisServiceMock = new();
    private readonly Mock<ILiveKitTokenService> _tokenServiceMock = new();
    private readonly FakeBreakoutSessionRepository _sessionRepo = new();
    private readonly FakeBreakoutAssignmentRepository _assignmentRepo = new();
    private readonly BreakoutsService _sut;

    public BreakoutsServiceTests()
    {
        _redisServiceMock
            .Setup(r => r.PublishEventAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>()))
            .ReturnsAsync(Result.Success());
        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(null));

        _tokenServiceMock
            .Setup(t => t.GenerateToken(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Returns(Result.Success("fake-token"));

        _unitOfWorkMock.Setup(u => u.BreakoutSessionRepository).Returns(_sessionRepo);
        _unitOfWorkMock.Setup(u => u.BreakoutAssignmentRepository).Returns(_assignmentRepo);

        _sut = new BreakoutsService(_unitOfWorkMock.Object, _grpcServiceMock.Object, _redisServiceMock.Object, _tokenServiceMock.Object);
    }

    private MeetingRoom SetupRoom(Guid translationRoomId, Guid? activeHostId = null)
    {
        var meetingRoom = new MeetingRoom { Id = Guid.NewGuid(), TranslationRoomId = translationRoomId, ActiveHostId = activeHostId, ProviderRoomName = translationRoomId.ToString() };
        var roomRepoMock = new Mock<IMeetingRoomRepository>();
        roomRepoMock
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(meetingRoom);
        roomRepoMock
            .Setup(r => r.GetByIdAsync(meetingRoom.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(meetingRoom);
        _unitOfWorkMock.Setup(u => u.MeetingRoomRepository).Returns(roomRepoMock.Object);
        return meetingRoom;
    }

    /// <summary>IsHostAsync always calls GetRoomDetailsAsync (redis cache is mocked empty above) —
    /// mirrors PollsServiceTests' own pattern.</summary>
    private void SetupHostGrpc(Guid translationRoomId, Guid originalHostUserId)
    {
        _grpcServiceMock
            .Setup(g => g.GetRoomDetailsAsync(translationRoomId))
            .ReturnsAsync(Result.Success(new WarpTalk.Shared.Protos.GetTranslationRoomResponse { HostId = originalHostUserId.ToString() }));
    }

    [Fact]
    public async Task StartBreakoutsAsync_CreatesSessionsAndAssignments_WhenCallerIsHost()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        SetupRoom(translationRoomId, hostId);
        SetupHostGrpc(translationRoomId, Guid.NewGuid());

        var request = new CreateBreakoutsRequest
        {
            Groups = new List<BreakoutGroupRequest>
            {
                new() { Label = "Group A", UserIds = new List<Guid> { alice } },
                new() { Label = "Group B", UserIds = new List<Guid> { bob } },
            },
            DurationSeconds = 300
        };

        var result = await _sut.StartBreakoutsAsync(translationRoomId, hostId, request);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Sessions.Count);
        Assert.Equal(2, _sessionRepo.Items.Count);
        Assert.Equal(2, _assignmentRepo.Items.Count);
        Assert.Contains(_sessionRepo.Items, s => s.ProviderRoomName == $"{translationRoomId}-breakout-1");
        Assert.Contains(_sessionRepo.Items, s => s.ProviderRoomName == $"{translationRoomId}-breakout-2");
        _redisServiceMock.Verify(
            r => r.PublishEventAsync(
                "warptalk:translation-room:commands",
                It.Is<Dictionary<string, object?>>(payload => HasProperty(payload, "Command", "BreakoutsStarted"))),
            Times.Once);
    }

    [Fact]
    public async Task StartBreakoutsAsync_ReturnsForbidden_WhenCallerIsNotHost()
    {
        var translationRoomId = Guid.NewGuid();
        var participant = Guid.NewGuid();
        SetupRoom(translationRoomId, Guid.NewGuid());
        SetupHostGrpc(translationRoomId, Guid.NewGuid());

        var request = new CreateBreakoutsRequest
        {
            Groups = new List<BreakoutGroupRequest> { new() { Label = "Group A", UserIds = new List<Guid> { Guid.NewGuid() } } }
        };

        var result = await _sut.StartBreakoutsAsync(translationRoomId, participant, request);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Empty(_sessionRepo.Items);
    }

    [Fact]
    public async Task StartBreakoutsAsync_ReturnsValidationError_WhenNoGroups()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        SetupRoom(translationRoomId, hostId);
        SetupHostGrpc(translationRoomId, Guid.NewGuid());

        var request = new CreateBreakoutsRequest { Groups = new List<BreakoutGroupRequest>() };

        var result = await _sut.StartBreakoutsAsync(translationRoomId, hostId, request);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public async Task StartBreakoutsAsync_ReturnsValidationError_WhenUserAssignedToMultipleGroups()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var duplicate = Guid.NewGuid();
        SetupRoom(translationRoomId, hostId);
        SetupHostGrpc(translationRoomId, Guid.NewGuid());

        var request = new CreateBreakoutsRequest
        {
            Groups = new List<BreakoutGroupRequest>
            {
                new() { Label = "Group A", UserIds = new List<Guid> { duplicate } },
                new() { Label = "Group B", UserIds = new List<Guid> { duplicate } },
            }
        };

        var result = await _sut.StartBreakoutsAsync(translationRoomId, hostId, request);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Empty(_sessionRepo.Items);
    }

    [Fact]
    public async Task StartBreakoutsAsync_ReturnsValidationError_WhenNoParticipantsAssigned()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        SetupRoom(translationRoomId, hostId);
        SetupHostGrpc(translationRoomId, Guid.NewGuid());

        var request = new CreateBreakoutsRequest
        {
            Groups = new List<BreakoutGroupRequest> { new() { Label = "Group A", UserIds = new List<Guid>() } }
        };

        var result = await _sut.StartBreakoutsAsync(translationRoomId, hostId, request);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public async Task StartBreakoutsAsync_EndsPreviousActiveSessions_WhenRestarted()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = SetupRoom(translationRoomId, hostId);
        SetupHostGrpc(translationRoomId, Guid.NewGuid());

        var staleSession = new BreakoutSession { Id = Guid.NewGuid(), ParentMeetingRoomId = room.Id, ProviderRoomName = "x", Label = "Stale", CreatedAt = DateTime.UtcNow };
        _sessionRepo.Items.Add(staleSession);

        var request = new CreateBreakoutsRequest
        {
            Groups = new List<BreakoutGroupRequest> { new() { Label = "Group A", UserIds = new List<Guid> { Guid.NewGuid() } } }
        };

        var result = await _sut.StartBreakoutsAsync(translationRoomId, hostId, request);

        Assert.True(result.IsSuccess);
        Assert.NotNull(staleSession.EndedAt);
    }

    [Fact]
    public async Task EndBreakoutsAsync_EndsActiveSessions_WhenCallerIsHost()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = SetupRoom(translationRoomId, hostId);
        SetupHostGrpc(translationRoomId, Guid.NewGuid());

        var session = new BreakoutSession { Id = Guid.NewGuid(), ParentMeetingRoomId = room.Id, ProviderRoomName = "x", Label = "Group A", CreatedAt = DateTime.UtcNow };
        _sessionRepo.Items.Add(session);

        var result = await _sut.EndBreakoutsAsync(translationRoomId, hostId);

        Assert.True(result.IsSuccess);
        Assert.NotNull(session.EndedAt);
        _redisServiceMock.Verify(
            r => r.PublishEventAsync(
                "warptalk:translation-room:commands",
                It.Is<Dictionary<string, object?>>(payload => HasProperty(payload, "Command", "BreakoutsEnded"))),
            Times.Once);
    }

    [Fact]
    public async Task EndBreakoutsAsync_ReturnsForbidden_WhenCallerIsNotHost()
    {
        var translationRoomId = Guid.NewGuid();
        var room = SetupRoom(translationRoomId, Guid.NewGuid());
        SetupHostGrpc(translationRoomId, Guid.NewGuid());

        var session = new BreakoutSession { Id = Guid.NewGuid(), ParentMeetingRoomId = room.Id, ProviderRoomName = "x", Label = "Group A", CreatedAt = DateTime.UtcNow };
        _sessionRepo.Items.Add(session);

        var result = await _sut.EndBreakoutsAsync(translationRoomId, Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Null(session.EndedAt);
    }

    [Fact]
    public async Task ExpireDueBreakoutsAsync_EndsOnlyElapsedSessionsAndRelaysToParentRoom()
    {
        var now = DateTime.UtcNow;
        var translationRoomId = Guid.NewGuid();
        var room = SetupRoom(translationRoomId);
        var expired = new BreakoutSession
        {
            Id = Guid.NewGuid(),
            ParentMeetingRoomId = room.Id,
            ProviderRoomName = "expired",
            Label = "Expired",
            StartedAt = now.AddMinutes(-10),
            DurationSeconds = 60,
            CreatedAt = now.AddMinutes(-10)
        };
        var active = new BreakoutSession
        {
            Id = Guid.NewGuid(),
            ParentMeetingRoomId = room.Id,
            ProviderRoomName = "active",
            Label = "Active",
            StartedAt = now,
            DurationSeconds = 300,
            CreatedAt = now
        };
        _sessionRepo.Items.Add(expired);
        _sessionRepo.Items.Add(active);

        var result = await _sut.ExpireDueBreakoutsAsync(now);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);
        Assert.Equal(now, expired.EndedAt);
        Assert.Null(active.EndedAt);
        _redisServiceMock.Verify(
            redis => redis.PublishEventAsync(
                "warptalk:translation-room:commands",
                It.Is<Dictionary<string, object?>>(payload =>
                    HasProperty(payload, "Command", "BreakoutsEnded"))),
            Times.Once);
    }

    [Fact]
    public async Task GetMyAssignmentAsync_ReturnsToken_WhenActivelyAssigned()
    {
        var translationRoomId = Guid.NewGuid();
        var room = SetupRoom(translationRoomId);
        var userId = Guid.NewGuid();

        var session = new BreakoutSession { Id = Guid.NewGuid(), ParentMeetingRoomId = room.Id, ProviderRoomName = "room-breakout-1", Label = "Group A", DurationSeconds = 300, StartedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow };
        _sessionRepo.Items.Add(session);
        _assignmentRepo.Items.Add(new BreakoutAssignment { Id = Guid.NewGuid(), BreakoutSessionId = session.Id, UserId = userId, CreatedAt = DateTime.UtcNow });

        var result = await _sut.GetMyAssignmentAsync(translationRoomId, userId);

        Assert.True(result.IsSuccess);
        Assert.Equal("room-breakout-1", result.Value!.ProviderRoomName);
        Assert.Equal("fake-token", result.Value.Token);
    }

    [Fact]
    public async Task GetMyAssignmentAsync_ReturnsNotFound_WhenSessionAlreadyEnded()
    {
        var translationRoomId = Guid.NewGuid();
        var room = SetupRoom(translationRoomId);
        var userId = Guid.NewGuid();

        var session = new BreakoutSession { Id = Guid.NewGuid(), ParentMeetingRoomId = room.Id, ProviderRoomName = "room-breakout-1", Label = "Group A", EndedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow };
        _sessionRepo.Items.Add(session);
        _assignmentRepo.Items.Add(new BreakoutAssignment { Id = Guid.NewGuid(), BreakoutSessionId = session.Id, UserId = userId, CreatedAt = DateTime.UtcNow });

        var result = await _sut.GetMyAssignmentAsync(translationRoomId, userId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task GetMyAssignmentAsync_ReturnsNotFound_WhenNoAssignment()
    {
        var translationRoomId = Guid.NewGuid();
        SetupRoom(translationRoomId);

        var result = await _sut.GetMyAssignmentAsync(translationRoomId, Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
    }

    private static bool HasProperty(object payload, string name, string expectedValue)
    {
        if (payload is Dictionary<string, object?> dict)
            return dict.TryGetValue(name, out var value) && Equals(value?.ToString(), expectedValue);

        var prop = payload.GetType().GetProperty(name);
        return prop != null && Equals(prop.GetValue(payload)?.ToString(), expectedValue);
    }
}
