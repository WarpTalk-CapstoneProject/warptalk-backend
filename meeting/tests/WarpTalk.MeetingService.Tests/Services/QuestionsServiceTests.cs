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

public class QuestionsServiceTests
{
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly Mock<ITranslationRoomGrpcService> _grpcServiceMock = new();
    private readonly Mock<IRedisService> _redisServiceMock = new();
    private readonly FakeQuestionRepository _questionRepo = new();
    private readonly FakeQuestionVoteRepository _voteRepo = new();
    private readonly QuestionsService _sut;

    public QuestionsServiceTests()
    {
        _redisServiceMock
            .Setup(r => r.PublishEventAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>()))
            .ReturnsAsync(Result.Success());
        _redisServiceMock
            .Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(null));

        _unitOfWorkMock.Setup(u => u.QuestionRepository).Returns(_questionRepo);
        _unitOfWorkMock.Setup(u => u.QuestionVoteRepository).Returns(_voteRepo);

        _sut = new QuestionsService(_unitOfWorkMock.Object, _grpcServiceMock.Object, _redisServiceMock.Object);
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

    private void SetupHostGrpc(Guid translationRoomId, Guid originalHostUserId)
    {
        _grpcServiceMock
            .Setup(g => g.GetRoomDetailsAsync(translationRoomId))
            .ReturnsAsync(Result.Success(new WarpTalk.Shared.Protos.GetTranslationRoomResponse { HostId = originalHostUserId.ToString() }));
    }

    private MeetingParticipant SetupParticipant(MeetingRoom room, Guid userId, bool isActive = true)
    {
        var participant = new MeetingParticipant { Id = Guid.NewGuid(), MeetingRoomId = room.Id, UserId = userId, IsActive = isActive, ProviderIdentity = userId.ToString() };
        var participantRepoMock = new Mock<IMeetingParticipantRepository>();
        participantRepoMock
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(participant);
        _unitOfWorkMock.Setup(u => u.MeetingParticipantRepository).Returns(participantRepoMock.Object);
        return participant;
    }

    private void SetupNoParticipant(MeetingRoom room)
    {
        var participantRepoMock = new Mock<IMeetingParticipantRepository>();
        participantRepoMock
            .Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MeetingParticipant?)null);
        _unitOfWorkMock.Setup(u => u.MeetingParticipantRepository).Returns(participantRepoMock.Object);
    }

    [Fact]
    public async Task AskAsync_CreatesQuestion_WhenCallerIsActiveParticipant()
    {
        var translationRoomId = Guid.NewGuid();
        var askerId = Guid.NewGuid();
        var room = SetupRoom(translationRoomId);
        SetupParticipant(room, askerId);

        var result = await _sut.AskAsync(translationRoomId, askerId, new CreateQuestionRequest { Body = "When is the next release?", DisplayName = "Alice" });

        Assert.True(result.IsSuccess);
        Assert.Equal("Alice", result.Value!.AskedByDisplayName);
        Assert.Equal("open", result.Value.Status);
        Assert.Single(_questionRepo.Items);
        _redisServiceMock.Verify(
            r => r.PublishEventAsync(
                "warptalk:translation-room:commands",
                It.Is<Dictionary<string, object?>>(payload => HasProperty(payload, "Command", "QuestionAsked"))),
            Times.Once);
    }

    [Fact]
    public async Task AskAsync_ReturnsForbidden_WhenCallerIsNotAnActiveParticipant()
    {
        var translationRoomId = Guid.NewGuid();
        var room = SetupRoom(translationRoomId);
        SetupNoParticipant(room);

        var result = await _sut.AskAsync(translationRoomId, Guid.NewGuid(), new CreateQuestionRequest { Body = "Hello?" });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Empty(_questionRepo.Items);
    }

    [Fact]
    public async Task AskAsync_ReturnsValidationError_WhenBodyIsEmpty()
    {
        var translationRoomId = Guid.NewGuid();
        var askerId = Guid.NewGuid();

        var result = await _sut.AskAsync(translationRoomId, askerId, new CreateQuestionRequest { Body = "   " });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
    }

    [Fact]
    public async Task UpvoteAsync_TogglesUpvote_Idempotently()
    {
        var translationRoomId = Guid.NewGuid();
        var voterId = Guid.NewGuid();
        var room = SetupRoom(translationRoomId);
        SetupParticipant(room, voterId);

        var question = new Question { Id = Guid.NewGuid(), MeetingRoomId = room.Id, AskedBy = Guid.NewGuid(), AskedByDisplayName = "Bob", Body = "Q", Status = "open", CreatedAt = DateTime.UtcNow };
        _questionRepo.Items.Add(question);

        var firstToggle = await _sut.UpvoteAsync(translationRoomId, question.Id, voterId);
        Assert.True(firstToggle.IsSuccess);
        Assert.True(firstToggle.Value!.UpvotedByMe);
        Assert.Equal(1, firstToggle.Value.UpvoteCount);
        Assert.Single(_voteRepo.Items);

        var secondToggle = await _sut.UpvoteAsync(translationRoomId, question.Id, voterId);
        Assert.True(secondToggle.IsSuccess);
        Assert.False(secondToggle.Value!.UpvotedByMe);
        Assert.Equal(0, secondToggle.Value.UpvoteCount);
        Assert.Empty(_voteRepo.Items);
    }

    [Fact]
    public async Task AnswerAsync_MarksAnswered_WhenCallerIsHost()
    {
        var translationRoomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = SetupRoom(translationRoomId, hostId);
        SetupHostGrpc(translationRoomId, Guid.NewGuid());

        var question = new Question { Id = Guid.NewGuid(), MeetingRoomId = room.Id, AskedBy = Guid.NewGuid(), AskedByDisplayName = "Bob", Body = "Q", Status = "open", CreatedAt = DateTime.UtcNow };
        _questionRepo.Items.Add(question);

        var result = await _sut.AnswerAsync(translationRoomId, question.Id, hostId);

        Assert.True(result.IsSuccess);
        Assert.Equal("answered", question.Status);
        Assert.NotNull(question.AnsweredAt);
        _redisServiceMock.Verify(
            r => r.PublishEventAsync(
                "warptalk:translation-room:commands",
                It.Is<Dictionary<string, object?>>(payload => HasProperty(payload, "Command", "QuestionAnswered"))),
            Times.Once);
    }

    [Fact]
    public async Task AnswerAsync_ReturnsForbidden_WhenCallerIsNotHost()
    {
        var translationRoomId = Guid.NewGuid();
        var room = SetupRoom(translationRoomId, Guid.NewGuid());
        SetupHostGrpc(translationRoomId, Guid.NewGuid());

        var question = new Question { Id = Guid.NewGuid(), MeetingRoomId = room.Id, AskedBy = Guid.NewGuid(), AskedByDisplayName = "Bob", Body = "Q", Status = "open", CreatedAt = DateTime.UtcNow };
        _questionRepo.Items.Add(question);

        var result = await _sut.AnswerAsync(translationRoomId, question.Id, Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        Assert.Equal("open", question.Status);
    }

    [Fact]
    public async Task ListAsync_SortsByUpvoteCountDescending()
    {
        var translationRoomId = Guid.NewGuid();
        var room = SetupRoom(translationRoomId);

        var lowVotes = new Question { Id = Guid.NewGuid(), MeetingRoomId = room.Id, AskedBy = Guid.NewGuid(), AskedByDisplayName = "A", Body = "Low", Status = "open", CreatedAt = DateTime.UtcNow.AddMinutes(-2) };
        var highVotes = new Question { Id = Guid.NewGuid(), MeetingRoomId = room.Id, AskedBy = Guid.NewGuid(), AskedByDisplayName = "B", Body = "High", Status = "open", CreatedAt = DateTime.UtcNow.AddMinutes(-1) };
        _questionRepo.Items.AddRange(new[] { lowVotes, highVotes });
        _voteRepo.Items.Add(new QuestionVote { Id = Guid.NewGuid(), QuestionId = highVotes.Id, UserId = Guid.NewGuid() });
        _voteRepo.Items.Add(new QuestionVote { Id = Guid.NewGuid(), QuestionId = highVotes.Id, UserId = Guid.NewGuid() });

        var result = await _sut.ListAsync(translationRoomId, Guid.NewGuid());

        Assert.True(result.IsSuccess);
        Assert.Equal(highVotes.Id, result.Value![0].Id);
        Assert.Equal(2, result.Value[0].UpvoteCount);
        Assert.Equal(lowVotes.Id, result.Value[1].Id);
    }

    private static bool HasProperty(object payload, string name, string expectedValue)
    {
        // QuestionsService.PublishRelayAsync merges per-call fields into a Dictionary at
        // runtime (the extra-fields shape differs per command), unlike the anonymous-object
        // payloads used elsewhere in MeetingRoomService — so "Command" is a dictionary key
        // here, not a reflectable CLR property.
        if (payload is Dictionary<string, object?> dict)
            return dict.TryGetValue(name, out var value) && Equals(value?.ToString(), expectedValue);

        var prop = payload.GetType().GetProperty(name);
        return prop != null && Equals(prop.GetValue(payload)?.ToString(), expectedValue);
    }
}
