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

public class PollsServiceTests
{
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly Mock<ITranslationRoomGrpcService> _grpcServiceMock = new();
    private readonly Mock<IRedisService> _redisServiceMock = new();
    private readonly FakeGenericRepository<Poll> _pollRepo = new();
    private readonly FakeGenericRepository<PollOption> _optionRepo = new();
    private readonly FakeGenericRepository<PollVote> _voteRepo = new();
    private readonly PollsService _sut;

    public PollsServiceTests()
    {
        _redisServiceMock
            .Setup(r => r.PublishEventAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>()))
            .ReturnsAsync(Result.Success());
        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(null));

        _unitOfWorkMock.Setup(u => u.Repository<Poll>()).Returns(_pollRepo);
        _unitOfWorkMock.Setup(u => u.Repository<PollOption>()).Returns(_optionRepo);
        _unitOfWorkMock.Setup(u => u.Repository<PollVote>()).Returns(_voteRepo);

        _sut = new PollsService(_unitOfWorkMock.Object, _grpcServiceMock.Object, _redisServiceMock.Object);
    }

    private MeetingRoom SetupRoom(Guid translationRoomId, Guid? activeHostId = null)
    {
        var meetingRoom = new MeetingRoom { Id = Guid.NewGuid(), TranslationRoomId = translationRoomId, ActiveHostId = activeHostId, ProviderRoomName = translationRoomId.ToString() };
        var roomRepoMock = new Mock<IMeetingRoomRepository>();
        roomRepoMock
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(meetingRoom);
        _unitOfWorkMock.Setup(u => u.MeetingRoomRepository).Returns(roomRepoMock.Object);
        return meetingRoom;
    }

    /// <summary>IsHostAsync always calls GetRoomDetailsAsync (redis cache is mocked empty above) — set up
    /// even when the test relies on ActiveHostId, mirroring MeetingRoomServiceTests' own pattern.</summary>
    private void SetupHostGrpc(Guid translationRoomId, Guid originalHostUserId)
    {
        _grpcServiceMock
            .Setup(g => g.GetRoomDetailsAsync(translationRoomId))
            .ReturnsAsync(Result.Success(new WarpTalk.Shared.Protos.GetTranslationRoomResponse { HostId = originalHostUserId.ToString() }));
    }

    private void SetupParticipant(MeetingRoom room, Guid userId, bool isActive = true)
    {
        var participant = new MeetingParticipant { Id = Guid.NewGuid(), MeetingRoomId = room.Id, UserId = userId, IsActive = isActive };
        var participantRepoMock = new Mock<IMeetingParticipantRepository>();
        participantRepoMock
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(participant);
        _unitOfWorkMock.Setup(u => u.MeetingParticipantRepository).Returns(participantRepoMock.Object);
    }

    [Fact]
    public async Task CreatePollAsync_CreatesPoll_WhenCallerIsHost()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        SetupRoom(translationRoomId, hostId);
        SetupHostGrpc(translationRoomId, Guid.NewGuid());

        var request = new CreatePollRequest { Question = "Best pizza?", Options = new List<string> { "Pepperoni", "Margherita" }, IsMultipleChoice = false };

        var result = await _sut.CreatePollAsync(translationRoomId, hostId, request);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Options.Count);
        Assert.Equal("open", result.Value.Status);
        Assert.Single(_pollRepo.Items);
        Assert.Equal(2, _optionRepo.Items.Count);
        _redisServiceMock.Verify(
            r => r.PublishEventAsync(
                "warptalk:translation-room:commands",
                It.Is<Dictionary<string, object?>>(payload => HasProperty(payload, "Command", "PollCreated"))),
            Times.Once);
    }

    [Fact]
    public async Task CreatePollAsync_ReturnsForbidden_WhenCallerIsNotHost()
    {
        var translationRoomId = Guid.NewGuid();
        SetupRoom(translationRoomId, Guid.NewGuid());
        SetupHostGrpc(translationRoomId, Guid.NewGuid());

        var request = new CreatePollRequest { Question = "Best pizza?", Options = new List<string> { "Pepperoni", "Margherita" } };

        var result = await _sut.CreatePollAsync(translationRoomId, Guid.NewGuid(), request);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Empty(_pollRepo.Items);
    }

    [Fact]
    public async Task CreatePollAsync_ReturnsValidationError_WhenTooFewOptions()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();

        var request = new CreatePollRequest { Question = "Best pizza?", Options = new List<string> { "Only one" } };

        var result = await _sut.CreatePollAsync(translationRoomId, hostId, request);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public async Task CreatePollAsync_ReturnsValidationError_WhenTooManyOptions()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();

        var request = new CreatePollRequest { Question = "Best pizza?", Options = new List<string> { "A", "B", "C", "D", "E", "F", "G" } };

        var result = await _sut.CreatePollAsync(translationRoomId, hostId, request);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public async Task VoteAsync_RecordsVote_AndRevoteReplacesPriorVote()
    {
        var translationRoomId = Guid.NewGuid();
        var voterId = Guid.NewGuid();
        var room = SetupRoom(translationRoomId);
        SetupParticipant(room, voterId);

        var poll = new Poll { Id = Guid.NewGuid(), MeetingRoomId = room.Id, Question = "Q", Status = "open", IsMultipleChoice = false, CreatedAt = DateTime.UtcNow };
        var optionA = new PollOption { Id = Guid.NewGuid(), PollId = poll.Id, Label = "A", Position = 0 };
        var optionB = new PollOption { Id = Guid.NewGuid(), PollId = poll.Id, Label = "B", Position = 1 };
        _pollRepo.Items.Add(poll);
        _optionRepo.Items.AddRange(new[] { optionA, optionB });

        var firstVote = await _sut.VoteAsync(translationRoomId, poll.Id, voterId, new VotePollRequest { OptionIds = new List<Guid> { optionA.Id } });
        Assert.True(firstVote.IsSuccess);
        Assert.Single(_voteRepo.Items);
        Assert.Equal(optionA.Id, _voteRepo.Items[0].OptionId);

        var secondVote = await _sut.VoteAsync(translationRoomId, poll.Id, voterId, new VotePollRequest { OptionIds = new List<Guid> { optionB.Id } });
        Assert.True(secondVote.IsSuccess);
        Assert.Single(_voteRepo.Items);
        Assert.Equal(optionB.Id, _voteRepo.Items[0].OptionId);
    }

    [Fact]
    public async Task VoteAsync_RejectsMultipleOptions_ForSingleChoicePoll()
    {
        var translationRoomId = Guid.NewGuid();
        var voterId = Guid.NewGuid();
        var room = SetupRoom(translationRoomId);
        SetupParticipant(room, voterId);

        var poll = new Poll { Id = Guid.NewGuid(), MeetingRoomId = room.Id, Question = "Q", Status = "open", IsMultipleChoice = false, CreatedAt = DateTime.UtcNow };
        var optionA = new PollOption { Id = Guid.NewGuid(), PollId = poll.Id, Label = "A", Position = 0 };
        var optionB = new PollOption { Id = Guid.NewGuid(), PollId = poll.Id, Label = "B", Position = 1 };
        _pollRepo.Items.Add(poll);
        _optionRepo.Items.AddRange(new[] { optionA, optionB });

        var result = await _sut.VoteAsync(translationRoomId, poll.Id, voterId, new VotePollRequest { OptionIds = new List<Guid> { optionA.Id, optionB.Id } });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Empty(_voteRepo.Items);
    }

    [Fact]
    public async Task VoteAsync_AllowsMultipleOptions_ForMultipleChoicePoll()
    {
        var translationRoomId = Guid.NewGuid();
        var voterId = Guid.NewGuid();
        var room = SetupRoom(translationRoomId);
        SetupParticipant(room, voterId);

        var poll = new Poll { Id = Guid.NewGuid(), MeetingRoomId = room.Id, Question = "Q", Status = "open", IsMultipleChoice = true, CreatedAt = DateTime.UtcNow };
        var optionA = new PollOption { Id = Guid.NewGuid(), PollId = poll.Id, Label = "A", Position = 0 };
        var optionB = new PollOption { Id = Guid.NewGuid(), PollId = poll.Id, Label = "B", Position = 1 };
        _pollRepo.Items.Add(poll);
        _optionRepo.Items.AddRange(new[] { optionA, optionB });

        var result = await _sut.VoteAsync(translationRoomId, poll.Id, voterId, new VotePollRequest { OptionIds = new List<Guid> { optionA.Id, optionB.Id } });

        Assert.True(result.IsSuccess);
        Assert.Equal(2, _voteRepo.Items.Count);
    }

    [Fact]
    public async Task VoteAsync_ReturnsInvalidState_WhenPollClosed()
    {
        var translationRoomId = Guid.NewGuid();
        var voterId = Guid.NewGuid();
        var room = SetupRoom(translationRoomId);
        SetupParticipant(room, voterId);

        var poll = new Poll { Id = Guid.NewGuid(), MeetingRoomId = room.Id, Question = "Q", Status = "closed", CreatedAt = DateTime.UtcNow };
        var optionA = new PollOption { Id = Guid.NewGuid(), PollId = poll.Id, Label = "A", Position = 0 };
        _pollRepo.Items.Add(poll);
        _optionRepo.Items.Add(optionA);

        var result = await _sut.VoteAsync(translationRoomId, poll.Id, voterId, new VotePollRequest { OptionIds = new List<Guid> { optionA.Id } });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidState, result.ErrorCode);
    }

    [Fact]
    public async Task CloseAsync_ClosesPoll_WhenCallerIsHost()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = SetupRoom(translationRoomId, hostId);
        SetupHostGrpc(translationRoomId, Guid.NewGuid());

        var poll = new Poll { Id = Guid.NewGuid(), MeetingRoomId = room.Id, Question = "Q", Status = "open", CreatedAt = DateTime.UtcNow };
        _pollRepo.Items.Add(poll);

        var result = await _sut.CloseAsync(translationRoomId, poll.Id, hostId);

        Assert.True(result.IsSuccess);
        Assert.Equal("closed", poll.Status);
        Assert.NotNull(poll.ClosedAt);
        _redisServiceMock.Verify(
            r => r.PublishEventAsync(
                "warptalk:translation-room:commands",
                It.Is<Dictionary<string, object?>>(payload => HasProperty(payload, "Command", "PollClosed"))),
            Times.Once);
    }

    [Fact]
    public async Task CloseAsync_ReturnsForbidden_WhenCallerIsNotHost()
    {
        var translationRoomId = Guid.NewGuid();
        var room = SetupRoom(translationRoomId, Guid.NewGuid());
        SetupHostGrpc(translationRoomId, Guid.NewGuid());

        var poll = new Poll { Id = Guid.NewGuid(), MeetingRoomId = room.Id, Question = "Q", Status = "open", CreatedAt = DateTime.UtcNow };
        _pollRepo.Items.Add(poll);

        var result = await _sut.CloseAsync(translationRoomId, poll.Id, Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Equal("open", poll.Status);
    }

    private static bool HasProperty(object payload, string name, string expectedValue)
    {
        // PollsService.PublishRelayAsync merges per-call fields into a Dictionary at runtime
        // (the extra-fields shape differs per command), unlike the anonymous-object payloads
        // used elsewhere in MeetingRoomService — so "Command" is a dictionary key here, not a
        // reflectable CLR property.
        if (payload is Dictionary<string, object?> dict)
            return dict.TryGetValue(name, out var value) && Equals(value?.ToString(), expectedValue);

        var prop = payload.GetType().GetProperty(name);
        return prop != null && Equals(prop.GetValue(payload)?.ToString(), expectedValue);
    }
}
