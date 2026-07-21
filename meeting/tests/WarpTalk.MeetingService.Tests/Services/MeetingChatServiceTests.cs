using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Application.Services;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Enums;
using WarpTalk.MeetingService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.MeetingService.Tests.Services;

public class MeetingChatServiceTests
{
    private readonly Mock<IUnitOfWork> _unitOfWorkMock;
    private readonly Mock<IMeetingChatNotifier> _notifierMock;
    private readonly Mock<IRedisService> _redisMock;
    private readonly Mock<IMeetingRoomRepository> _roomRepoMock;
    private readonly Mock<IMeetingParticipantRepository> _participantRepoMock;
    private readonly Mock<IMeetingChatMessageRepository> _chatMessageRepoMock;
    private readonly Mock<IMeetingChatAssistantRequestRepository> _assistantRepoMock;
    private readonly Mock<IMeetingChatModerationEventRepository> _moderationRepoMock;
    private readonly Mock<IMeetingChatTranslationRepository> _translationRepoMock;
    private readonly Mock<IChatTranslator> _chatTranslatorMock;
    private readonly MeetingChatService _sut;

    private readonly Guid _roomId = Guid.NewGuid();
    private readonly Guid _hostId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    public MeetingChatServiceTests()
    {
        _unitOfWorkMock = new Mock<IUnitOfWork>();
        _notifierMock = new Mock<IMeetingChatNotifier>();
        _redisMock = new Mock<IRedisService>();
        _roomRepoMock = new Mock<IMeetingRoomRepository>();
        _participantRepoMock = new Mock<IMeetingParticipantRepository>();
        _chatMessageRepoMock = new Mock<IMeetingChatMessageRepository>();
        _assistantRepoMock = new Mock<IMeetingChatAssistantRequestRepository>();
        _moderationRepoMock = new Mock<IMeetingChatModerationEventRepository>();
        _translationRepoMock = new Mock<IMeetingChatTranslationRepository>();
        _chatTranslatorMock = new Mock<IChatTranslator>();

        _unitOfWorkMock.Setup(u => u.MeetingRoomRepository).Returns(_roomRepoMock.Object);
        _unitOfWorkMock.Setup(u => u.MeetingParticipantRepository).Returns(_participantRepoMock.Object);
        _unitOfWorkMock.Setup(u => u.MeetingChatMessageRepository).Returns(_chatMessageRepoMock.Object);
        _unitOfWorkMock.Setup(u => u.MeetingChatAssistantRequestRepository).Returns(_assistantRepoMock.Object);
        _unitOfWorkMock.Setup(u => u.MeetingChatModerationEventRepository).Returns(_moderationRepoMock.Object);
        _unitOfWorkMock.Setup(u => u.MeetingChatTranslationRepository).Returns(_translationRepoMock.Object);

        // SendMessageAsync always looks up the cached room to resolve WorkspaceId; an
        // unconfigured mock returns null (Result<T> is a class), which NREs on .Value.
        _redisMock.Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(WarpTalk.Shared.Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(null));

        _chatTranslatorMock.Setup(t => t.ModelName).Returns("gpt-4o-mini");
        _chatTranslatorMock.Setup(t => t.PromptVersion).Returns(1);

        _sut = new MeetingChatService(_unitOfWorkMock.Object, _notifierMock.Object, _redisMock.Object, _chatTranslatorMock.Object);
    }

    private MeetingRoom CreateRoom(Guid? createdBy = null) => new()
    {
        Id = _roomId,
        TranslationRoomId = Guid.NewGuid(),
        ProviderRoomName = "test-room",
        Status = MeetingStatus.Active.ToString(),
        IsActive = true,
        CreatedBy = createdBy ?? _hostId,
        CreatedAt = DateTime.UtcNow
    };

    private MeetingParticipant CreateParticipant(Guid userId, bool isActive = true) => new()
    {
        Id = Guid.NewGuid(),
        MeetingRoomId = _roomId,
        UserId = userId,
        ProviderIdentity = userId.ToString(),
        IsActive = isActive,
        JoinedAt = DateTime.UtcNow,
        LeftAt = null
    };

    // --- SendMessage Tests ---

    [Fact]
    public async Task SendMessageAsync_RoomNotFound_ReturnsFailure()
    {
        _roomRepoMock.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MeetingRoom?)null);

        var request = new SendMeetingChatMessageRequest { OriginalText = "hello", OriginalLanguage = "en" };
        var result = await _sut.SendMessageAsync(_roomId, _userId, request);

        Assert.False(result.IsSuccess);
        Assert.Equal("NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public async Task SendMessageAsync_NotActiveParticipant_ReturnsFailure()
    {
        _roomRepoMock.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRoom());

        _participantRepoMock.Setup(p => p.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<MeetingParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MeetingParticipant?)null);

        var request = new SendMeetingChatMessageRequest { OriginalText = "hello", OriginalLanguage = "en" };
        var result = await _sut.SendMessageAsync(_roomId, _userId, request);

        Assert.False(result.IsSuccess);
        Assert.Equal("FORBIDDEN", result.ErrorCode);
    }

    [Fact]
    public async Task SendMessageAsync_HostCanSendMessage_Success()
    {
        _roomRepoMock.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRoom(_hostId));

        _participantRepoMock.Setup(p => p.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<MeetingParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateParticipant(_hostId));

        _unitOfWorkMock.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var request = new SendMeetingChatMessageRequest { OriginalText = "hello host", OriginalLanguage = "en" };
        var result = await _sut.SendMessageAsync(_roomId, _hostId, request);

        Assert.True(result.IsSuccess);
        Assert.Equal("hello host", result.Value!.OriginalText);
        _notifierMock.Verify(n => n.BroadcastMessageReceivedAsync(_roomId, It.IsAny<MeetingChatMessageDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendMessageAsync_ActiveParticipantCanSendMessage_Success()
    {
        _roomRepoMock.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRoom());

        _participantRepoMock.Setup(p => p.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<MeetingParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateParticipant(_userId));

        _unitOfWorkMock.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var request = new SendMeetingChatMessageRequest { OriginalText = "hello world", OriginalLanguage = "vi" };
        var result = await _sut.SendMessageAsync(_roomId, _userId, request);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("hello world", result.Value!.OriginalText);
    }

    [Fact]
    public async Task SendMessageAsync_WithWarpbotMention_PublishesAssistantEvent()
    {
        _roomRepoMock.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRoom());

        _participantRepoMock.Setup(p => p.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<MeetingParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateParticipant(_userId));

        _unitOfWorkMock.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var request = new SendMeetingChatMessageRequest
        {
            OriginalText = "@WarpBot summarize",
            OriginalLanguage = "en",
            Mentions = new List<ChatMentionDto> 
            {
                new ChatMentionDto { Id = "warpbot", Display = "WarpBot", Type = "agent" }
            }
        };

        var result = await _sut.SendMessageAsync(_roomId, _userId, request);

        Assert.True(result.IsSuccess);
        _redisMock.Verify(r => r.PublishEventAsync("meeting.chat.assistant_requested", It.IsAny<object>()), Times.Once);
        _assistantRepoMock.Verify(r => r.AddAsync(It.IsAny<MeetingChatAssistantRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- ModerateMessage Tests ---

    [Fact]
    public async Task ModerateMessageAsync_NonHost_ReturnsFailure()
    {
        _roomRepoMock.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRoom());

        var result = await _sut.ModerateMessageAsync(_roomId, Guid.NewGuid(), _userId,
            new ModerateMeetingChatMessageRequest { Reason = "spam" });

        Assert.False(result.IsSuccess);
        Assert.Equal("FORBIDDEN", result.ErrorCode);
    }

    [Fact]
    public async Task ModerateMessageAsync_Host_HidesMessage_BroadcastsEvent()
    {
        var messageId = Guid.NewGuid();
        var message = new MeetingChatMessage
        {
            Id = messageId,
            MeetingRoomId = _roomId,
            SenderUserId = _userId,
            OriginalText = "bad message",
            OriginalLanguage = "en",
            SenderType = "user",
            MessageType = "text",
            IsHidden = false,
            CreatedAt = DateTime.UtcNow
        };

        _roomRepoMock.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRoom(_hostId));

        _chatMessageRepoMock.Setup(r => r.GetByIdAsync(messageId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(message);

        _unitOfWorkMock.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var result = await _sut.ModerateMessageAsync(_roomId, messageId, _hostId,
            new ModerateMeetingChatMessageRequest { Reason = "inappropriate" });

        Assert.True(result.IsSuccess);
        Assert.True(message.IsHidden);
        _notifierMock.Verify(n => n.BroadcastMessageHiddenAsync(_roomId, messageId, It.IsAny<CancellationToken>()), Times.Once);
        _moderationRepoMock.Verify(r => r.AddAsync(It.IsAny<MeetingChatModerationEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- RequestTranslation Tests ---

    private MeetingChatMessage CreateMessage(Guid messageId, string text = "hello", string language = "en") => new()
    {
        Id = messageId,
        MeetingRoomId = _roomId,
        OriginalText = text,
        OriginalLanguage = language,
        SenderType = "user",
        MessageType = "text",
        CreatedAt = DateTime.UtcNow
    };

    private void SetupAuthorizedRequest(Guid messageId, MeetingChatMessage message)
    {
        _roomRepoMock.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRoom());

        _participantRepoMock.Setup(p => p.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<MeetingParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateParticipant(_hostId));

        _chatMessageRepoMock.Setup(r => r.GetByIdAsync(messageId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(message);
    }

    [Fact]
    public async Task RequestTranslationAsync_SameLanguage_ReturnsOriginalWithoutCallingTranslator()
    {
        var messageId = Guid.NewGuid();
        SetupAuthorizedRequest(messageId, CreateMessage(messageId, "hello", "en"));

        var request = new TranslateMeetingChatMessageRequest { TargetLanguage = "en" };
        var result = await _sut.RequestTranslationAsync(_roomId, messageId, _hostId, request);

        Assert.True(result.IsSuccess);
        Assert.Equal("hello", result.Value!.TranslatedText);
        Assert.False(result.Value!.Cached);
        _chatTranslatorMock.Verify(t => t.TranslateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RequestTranslationAsync_CacheHit_ReturnsCachedTranslationWithoutCallingTranslator()
    {
        var messageId = Guid.NewGuid();
        SetupAuthorizedRequest(messageId, CreateMessage(messageId, "hello", "en"));

        _translationRepoMock.Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<MeetingChatTranslation, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MeetingChatTranslation
            {
                Id = Guid.NewGuid(),
                MessageId = messageId,
                MeetingRoomId = _roomId,
                SourceLanguage = "en",
                TargetLanguage = "vi",
                TranslatedText = "xin chào",
                CreatedAt = DateTime.UtcNow,
            });

        var request = new TranslateMeetingChatMessageRequest { TargetLanguage = "vi" };
        var result = await _sut.RequestTranslationAsync(_roomId, messageId, _hostId, request);

        Assert.True(result.IsSuccess);
        Assert.Equal("xin chào", result.Value!.TranslatedText);
        Assert.True(result.Value!.Cached);
        _chatTranslatorMock.Verify(t => t.TranslateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RequestTranslationAsync_CacheMiss_CallsTranslatorAndPersistsResult()
    {
        var messageId = Guid.NewGuid();
        SetupAuthorizedRequest(messageId, CreateMessage(messageId, "hello", "en"));

        _translationRepoMock.Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<MeetingChatTranslation, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MeetingChatTranslation?)null);

        _chatTranslatorMock.Setup(t => t.TranslateAsync("hello", "en", "vi", It.IsAny<CancellationToken>()))
            .ReturnsAsync(WarpTalk.Shared.Result.Success("xin chào"));

        _unitOfWorkMock.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var request = new TranslateMeetingChatMessageRequest { TargetLanguage = "vi" };
        var result = await _sut.RequestTranslationAsync(_roomId, messageId, _hostId, request);

        Assert.True(result.IsSuccess);
        Assert.Equal("xin chào", result.Value!.TranslatedText);
        Assert.False(result.Value!.Cached);
        _translationRepoMock.Verify(r => r.AddAsync(
            It.Is<MeetingChatTranslation>(t => t.MessageId == messageId && t.TargetLanguage == "vi" && t.TranslatedText == "xin chào" && t.ModelUsed == "gpt-4o-mini"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RequestTranslationAsync_TranslatorFails_ReturnsFailureWithoutPersisting()
    {
        var messageId = Guid.NewGuid();
        SetupAuthorizedRequest(messageId, CreateMessage(messageId, "hello", "en"));

        _translationRepoMock.Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<MeetingChatTranslation, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MeetingChatTranslation?)null);

        _chatTranslatorMock.Setup(t => t.TranslateAsync("hello", "en", "vi", It.IsAny<CancellationToken>()))
            .ReturnsAsync(WarpTalk.Shared.Result.Failure<string>("Translation service is currently unavailable.", "TRANSLATION_FAILED"));

        var request = new TranslateMeetingChatMessageRequest { TargetLanguage = "vi" };
        var result = await _sut.RequestTranslationAsync(_roomId, messageId, _hostId, request);

        Assert.False(result.IsSuccess);
        Assert.Equal("TRANSLATION_FAILED", result.ErrorCode);
        _translationRepoMock.Verify(r => r.AddAsync(It.IsAny<MeetingChatTranslation>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
