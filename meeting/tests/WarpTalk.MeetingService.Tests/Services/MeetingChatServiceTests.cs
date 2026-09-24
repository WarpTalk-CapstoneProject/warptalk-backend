using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Moq;
using WarpTalk.MeetingService.Application.DTOs;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Application.Services;
using WarpTalk.MeetingService.Domain.Constants;
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
    private readonly Mock<IRtcStreamParticipantRepository> _participantRepoMock;
    private readonly Mock<IMeetingChatMessageRepository> _chatMessageRepoMock;
    private readonly Mock<IMeetingChatAssistantRequestRepository> _assistantRepoMock;
    private readonly Mock<IMeetingChatModerationEventRepository> _moderationRepoMock;
    private readonly Mock<IMeetingChatTranslationRepository> _translationRepoMock;
    private readonly Mock<IChatTranslator> _chatTranslatorMock;
    private readonly Mock<IMeetingChatFileStorage> _fileStorageMock;
    private readonly Mock<IRtcSessionRevocationRepository> _revocationRepoMock = new();
    private readonly List<RtcSessionRevocation> _revocations = new();
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
        _participantRepoMock = new Mock<IRtcStreamParticipantRepository>();
        _chatMessageRepoMock = new Mock<IMeetingChatMessageRepository>();
        _assistantRepoMock = new Mock<IMeetingChatAssistantRequestRepository>();
        _moderationRepoMock = new Mock<IMeetingChatModerationEventRepository>();
        _translationRepoMock = new Mock<IMeetingChatTranslationRepository>();
        _chatTranslatorMock = new Mock<IChatTranslator>();
        _fileStorageMock = new Mock<IMeetingChatFileStorage>();

        _unitOfWorkMock.Setup(u => u.MeetingRoomRepository).Returns(_roomRepoMock.Object);
        _unitOfWorkMock.Setup(u => u.RtcStreamParticipantRepository).Returns(_participantRepoMock.Object);
        _unitOfWorkMock.Setup(u => u.MeetingChatMessageRepository).Returns(_chatMessageRepoMock.Object);
        _unitOfWorkMock.Setup(u => u.MeetingChatAssistantRequestRepository).Returns(_assistantRepoMock.Object);
        _unitOfWorkMock.Setup(u => u.MeetingChatModerationEventRepository).Returns(_moderationRepoMock.Object);
        _unitOfWorkMock.Setup(u => u.MeetingChatTranslationRepository).Returns(_translationRepoMock.Object);
        _unitOfWorkMock.Setup(u => u.RtcSessionRevocationRepository).Returns(_revocationRepoMock.Object);
        _revocationRepoMock
            .Setup(r => r.AnyAsync(It.IsAny<Expression<Func<RtcSessionRevocation, bool>>>(), It.IsAny<CancellationToken>()))
            .Returns((Expression<Func<RtcSessionRevocation, bool>> predicate, CancellationToken _) =>
                Task.FromResult(_revocations.Any(predicate.Compile())));

        // SendMessageAsync always looks up the cached room to resolve WorkspaceId; an
        // unconfigured mock returns null (Result<T> is a class), which NREs on .Value.
        _redisMock.Setup(r => r.GetCacheAsync<WarpTalk.Shared.Protos.GetTranslationRoomResponse>(It.IsAny<string>()))
            .ReturnsAsync(WarpTalk.Shared.Result.Success<WarpTalk.Shared.Protos.GetTranslationRoomResponse?>(null));

        _chatTranslatorMock.Setup(t => t.ModelName).Returns("gpt-4o-mini");
        _chatTranslatorMock.Setup(t => t.PromptVersion).Returns(1);

        _sut = new MeetingChatService(_unitOfWorkMock.Object, _notifierMock.Object, _redisMock.Object, _chatTranslatorMock.Object, _fileStorageMock.Object);
    }

    private static IFormFile CreateFormFile(string fileName, string contentType, int sizeBytes)
    {
        var content = new byte[sizeBytes];
        var stream = new MemoryStream(content);
        return new FormFile(stream, 0, sizeBytes, "file", fileName) { Headers = new Microsoft.AspNetCore.Http.HeaderDictionary(), ContentType = contentType };
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

    private RtcStreamParticipant CreateParticipant(Guid userId, bool isActive = true) => new()
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
    public async Task SendMessageAsync_MessageOverLengthLimit_ReturnsValidationFailure()
    {
        var request = new SendMeetingChatMessageRequest
        {
            OriginalText = new string('a', MeetingChatConstants.MaxMessageLength + 1),
            OriginalLanguage = "en"
        };

        var result = await _sut.SendMessageAsync(_roomId, _userId, request);

        Assert.False(result.IsSuccess);
        Assert.Equal("VALIDATION_ERROR", result.ErrorCode);
        // Rejected before the room is even looked up — no point paying for the query.
        _roomRepoMock.Verify(r => r.FirstOrDefaultAsync(
            It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SendMessageAsync_MessageExactlyAtLengthLimit_IsAccepted()
    {
        _roomRepoMock.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRoom());
        _participantRepoMock.Setup(p => p.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateParticipant(_userId));

        var request = new SendMeetingChatMessageRequest
        {
            OriginalText = new string('a', MeetingChatConstants.MaxMessageLength),
            OriginalLanguage = "en"
        };

        var result = await _sut.SendMessageAsync(_roomId, _userId, request);

        Assert.True(result.IsSuccess);
    }

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
                It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RtcStreamParticipant?)null);

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
                It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateParticipant(_hostId));

        _unitOfWorkMock.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _redisMock.Setup(r => r.PublishStreamMessageAsync(
                "assistant:chat_requests",
                It.IsAny<Dictionary<string, string>>()))
            .ReturnsAsync(WarpTalk.Shared.Result.Success());

        var request = new SendMeetingChatMessageRequest { OriginalText = "hello host", OriginalLanguage = "en" };
        var result = await _sut.SendMessageAsync(_roomId, _hostId, request);

        Assert.True(result.IsSuccess);
        Assert.Equal("hello host", result.Value!.OriginalText);
        _notifierMock.Verify(n => n.BroadcastMessageReceivedAsync(_roomId, It.IsAny<MeetingChatMessageDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The status an @agent mention is recorded with must be one
    /// MeetingChatAssistantResultConsumerService acts on.
    ///
    /// It was written as "pending" and the consumer only announces "WarpBot is thinking" for
    /// a request still sitting at "queued". The two spellings never met, so the indicator
    /// never once fired in production — and its absence was read as "the mention was never
    /// sent", which sent a live investigation down the wrong half of the pipeline.
    /// </summary>
    [Fact]
    public async Task SendMessageAsync_AgentMention_RecordsAStatusTheConsumerActsOn()
    {
        _roomRepoMock.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRoom(_hostId));

        _participantRepoMock.Setup(p => p.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateParticipant(_hostId));

        _unitOfWorkMock.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _redisMock.Setup(r => r.PublishStreamMessageAsync(
                "assistant:chat_requests",
                It.IsAny<Dictionary<string, string>>()))
            .ReturnsAsync(WarpTalk.Shared.Result.Success());

        MeetingChatAssistantRequest? captured = null;
        _assistantRepoMock
            .Setup(repo => repo.AddAsync(It.IsAny<MeetingChatAssistantRequest>(), It.IsAny<CancellationToken>()))
            .Callback<MeetingChatAssistantRequest, CancellationToken>((entity, _) => captured = entity);

        var request = new SendMeetingChatMessageRequest
        {
            OriginalText = "@WarpBot hello",
            OriginalLanguage = "en",
            Mentions = new List<ChatMentionDto>
            {
                new() { Id = "bot-warpbot", Display = "WarpBot", Type = "agent" }
            }
        };

        var result = await _sut.SendMessageAsync(_roomId, _hostId, request);

        Assert.True(result.IsSuccess);
        Assert.NotNull(captured);
        Assert.Equal("queued", captured!.Status);
    }

    [Fact]
    public async Task SendMessageAsync_ActiveParticipantCanSendMessage_Success()
    {
        _roomRepoMock.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRoom());

        _participantRepoMock.Setup(p => p.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
                It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateParticipant(_userId));

        _unitOfWorkMock.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _redisMock.Setup(r => r.PublishStreamMessageAsync(
                "assistant:chat_requests",
                It.IsAny<Dictionary<string, string>>()))
            .ReturnsAsync(WarpTalk.Shared.Result.Success());

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
        _redisMock.Verify(r => r.PublishStreamMessageAsync(
            "assistant:chat_requests",
            It.Is<Dictionary<string, string>>(fields =>
                fields["request_id"] != string.Empty
                && fields["conversation_id"] != string.Empty
                && fields["origin"] == "meeting_chat"
                && fields.ContainsKey("history_json"))), Times.Once);
        _assistantRepoMock.Verify(r => r.AddAsync(It.IsAny<MeetingChatAssistantRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendMessageAsync_WhenAssistantStreamIsUnavailable_KeepsUserMessageAndPersistsFailureReply()
    {
        _roomRepoMock.Setup(r => r.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<MeetingRoom, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRoom());
        _participantRepoMock.Setup(p => p.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateParticipant(_userId));
        _unitOfWorkMock.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _redisMock.Setup(r => r.PublishStreamMessageAsync(
                "assistant:chat_requests",
                It.IsAny<Dictionary<string, string>>()))
            .ReturnsAsync(WarpTalk.Shared.Result.Failure("offline", "REDIS_ERROR"));

        var persistedMessages = new List<MeetingChatMessage>();
        _chatMessageRepoMock.Setup(r => r.AddAsync(
                It.IsAny<MeetingChatMessage>(),
                It.IsAny<CancellationToken>()))
            .Callback<MeetingChatMessage, CancellationToken>((message, _) => persistedMessages.Add(message))
            .Returns(Task.CompletedTask);

        var request = new SendMeetingChatMessageRequest
        {
            OriginalText = "@WarpBot summarize",
            OriginalLanguage = "en",
            Mentions =
            [
                new ChatMentionDto { Id = "warpbot", Display = "WarpBot", Type = "agent" }
            ]
        };

        var result = await _sut.SendMessageAsync(_roomId, _userId, request);

        Assert.True(result.IsSuccess);
        Assert.Contains(persistedMessages, message =>
            message.SenderType == "assistant"
            && message.MessageType == "assistant_response"
            && message.OriginalText.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
        _notifierMock.Verify(n => n.BroadcastMessageReceivedAsync(
            _roomId,
            It.Is<MeetingChatMessageDto>(message => message.SenderType == "assistant"),
            It.IsAny<CancellationToken>()), Times.Once);
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
                It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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

    // --- UploadFile Tests ---

    [Fact]
    public async Task UploadFileAsync_RejectsBlockedExtension()
    {
        var file = CreateFormFile("virus.exe", "application/octet-stream", 100);
        var request = new UploadMeetingChatFileRequest { File = file };

        var result = await _sut.UploadFileAsync(_roomId, _hostId, request);

        Assert.False(result.IsSuccess);
        Assert.Equal("VALIDATION_ERROR", result.ErrorCode);
        _roomRepoMock.Verify(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UploadFileAsync_RejectsOversizedFile()
    {
        var file = CreateFormFile("big.zip", "application/zip", 26 * 1024 * 1024);
        var request = new UploadMeetingChatFileRequest { File = file };

        var result = await _sut.UploadFileAsync(_roomId, _hostId, request);

        Assert.False(result.IsSuccess);
        Assert.Equal("VALIDATION_ERROR", result.ErrorCode);
    }

    [Fact]
    public async Task UploadFileAsync_NotActiveParticipant_ReturnsFailure()
    {
        _roomRepoMock.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRoom());

        _participantRepoMock.Setup(p => p.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RtcStreamParticipant?)null);

        var file = CreateFormFile("doc.pdf", "application/pdf", 100);
        var request = new UploadMeetingChatFileRequest { File = file };

        var result = await _sut.UploadFileAsync(_roomId, _userId, request);

        Assert.False(result.IsSuccess);
        Assert.Equal("FORBIDDEN", result.ErrorCode);
    }

    [Fact]
    public async Task UploadFileAsync_ActiveParticipant_SavesAndBroadcasts()
    {
        _roomRepoMock.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRoom());

        _participantRepoMock.Setup(p => p.FirstOrDefaultAsync(
                It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateParticipant(_userId));

        _unitOfWorkMock.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var file = CreateFormFile("notes.pdf", "application/pdf", 100);
        var request = new UploadMeetingChatFileRequest { File = file };

        var result = await _sut.UploadFileAsync(_roomId, _userId, request);

        Assert.True(result.IsSuccess);
        Assert.Equal("file", result.Value!.MessageType);
        Assert.Equal("notes.pdf", result.Value!.FileName);
        Assert.Equal(100, result.Value!.FileSizeBytes);
        _fileStorageMock.Verify(s => s.SaveAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
        _notifierMock.Verify(n => n.BroadcastMessageReceivedAsync(_roomId, It.IsAny<MeetingChatMessageDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- DownloadFile Tests ---

    [Fact]
    public async Task DownloadFileAsync_MessageNotFound_ReturnsFailure()
    {
        _roomRepoMock.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRoom(_hostId));

        _chatMessageRepoMock.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MeetingChatMessage?)null);

        var result = await _sut.DownloadFileAsync(_roomId, Guid.NewGuid(), _hostId);

        Assert.False(result.IsSuccess);
        Assert.Equal("NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public async Task DownloadFileAsync_FileMessage_ReturnsStream()
    {
        var messageId = Guid.NewGuid();
        var message = new MeetingChatMessage
        {
            Id = messageId,
            MeetingRoomId = _roomId,
            MessageType = "file",
            FileName = "notes.pdf",
            ContentType = "application/pdf",
            OriginalText = "notes.pdf",
            OriginalLanguage = "en",
            SenderType = "user",
            CreatedAt = DateTime.UtcNow
        };

        _roomRepoMock.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateRoom(_hostId));

        _chatMessageRepoMock.Setup(r => r.GetByIdAsync(messageId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(message);

        _fileStorageMock.Setup(s => s.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream());

        var result = await _sut.DownloadFileAsync(_roomId, messageId, _hostId);

        Assert.True(result.IsSuccess);
        Assert.Equal("notes.pdf", result.Value!.FileName);
        Assert.Equal("application/pdf", result.Value!.ContentType);
    }

    // --- GetRoomMessages Tests ---

    private MeetingChatMessage CreateMessage(string text, DateTime createdAt, bool isHidden = false) => new()
    {
        Id = Guid.NewGuid(),
        MeetingRoomId = _roomId,
        WorkspaceId = Guid.NewGuid(),
        SenderUserId = _hostId,
        SenderDisplayName = "Host",
        SenderType = "user",
        MessageType = "text",
        OriginalLanguage = "en",
        OriginalText = text,
        IsHidden = isHidden,
        CreatedAt = createdAt,
        Mentions = string.Empty
    };

    /// <summary>
    /// Backs the three repository lookups GetRoomMessagesAsync makes with in-memory lists and evaluates
    /// the service's own predicates, so the TranslationRoomId / MeetingRoomId / UserId filters are exercised.
    /// Returns the TranslationRoomId the caller should pass as the route room id.
    /// </summary>
    private Guid SetupRoomMessages(MeetingRoom? room, IEnumerable<RtcStreamParticipant> participants, IEnumerable<MeetingChatMessage> messages)
    {
        var translationRoomId = room?.TranslationRoomId ?? Guid.NewGuid();
        var rooms = room == null ? new List<MeetingRoom>() : new List<MeetingRoom> { room };
        var participantList = participants.ToList();
        var messageList = messages.ToList();

        _roomRepoMock.Setup(r => r.FirstOrDefaultAsync(It.IsAny<Expression<Func<MeetingRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((Expression<Func<MeetingRoom, bool>> predicate, string _, CancellationToken _) =>
                Task.FromResult(rooms.FirstOrDefault(predicate.Compile())));
        _participantRepoMock.Setup(p => p.FirstOrDefaultAsync(It.IsAny<Expression<Func<RtcStreamParticipant, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((Expression<Func<RtcStreamParticipant, bool>> predicate, string _, CancellationToken _) =>
                Task.FromResult(participantList.FirstOrDefault(predicate.Compile())));
        _chatMessageRepoMock.Setup(m => m.FindAsync(It.IsAny<Expression<Func<MeetingChatMessage, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((Expression<Func<MeetingChatMessage, bool>> predicate, string _, CancellationToken _) =>
                Task.FromResult<IReadOnlyList<MeetingChatMessage>>(messageList.Where(predicate.Compile()).ToList()));

        return translationRoomId;
    }

    [Fact]
    public async Task GetRoomMessagesAsync_HostWithVisibleMessages_ReturnsMessages()
    {
        var now = DateTime.UtcNow;
        var translationRoomId = SetupRoomMessages(
            CreateRoom(_hostId),
            Array.Empty<RtcStreamParticipant>(),
            new[] { CreateMessage("hello", now.AddMinutes(-2)), CreateMessage("world", now.AddMinutes(-1)) });

        var result = await _sut.GetRoomMessagesAsync(translationRoomId, _hostId);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "hello", "world" }, result.Value!.Select(m => m.OriginalText));
    }

    [Fact]
    public async Task GetRoomMessagesAsync_RoomNotFound_ReturnsNotFound()
    {
        var translationRoomId = SetupRoomMessages(null, Array.Empty<RtcStreamParticipant>(), Array.Empty<MeetingChatMessage>());

        var result = await _sut.GetRoomMessagesAsync(translationRoomId, _hostId);

        Assert.False(result.IsSuccess);
        Assert.Equal("NOT_FOUND", result.ErrorCode);
        _chatMessageRepoMock.Verify(m => m.FindAsync(
            It.IsAny<Expression<Func<MeetingChatMessage, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetRoomMessagesAsync_ParticipantWithVisibleMessages_ReturnsMessages()
    {
        var translationRoomId = SetupRoomMessages(
            CreateRoom(_hostId),
            new[] { CreateParticipant(_userId) },
            new[] { CreateMessage("hello", DateTime.UtcNow) });

        var result = await _sut.GetRoomMessagesAsync(translationRoomId, _userId);

        Assert.True(result.IsSuccess);
        Assert.Equal("hello", Assert.Single(result.Value!).OriginalText);
    }

    [Fact]
    public async Task GetRoomMessagesAsync_ParticipantWhoLeft_StillReturnsMessages()
    {
        var leftParticipant = CreateParticipant(_userId, isActive: false);
        leftParticipant.LeftAt = DateTime.UtcNow;
        var translationRoomId = SetupRoomMessages(
            CreateRoom(_hostId),
            new[] { leftParticipant },
            new[] { CreateMessage("hello", DateTime.UtcNow) });

        var result = await _sut.GetRoomMessagesAsync(translationRoomId, _userId);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!);
    }

    /// <summary>
    /// WT-699 / TC2504. The participant row survives a kick (it is only deactivated), so "ever
    /// joined" let a removed person keep reading the room's chat. The kick's REVOKED grant is what
    /// the read now refuses on.
    /// </summary>
    [Fact]
    public async Task GetRoomMessagesAsync_KickedParticipant_ReturnsForbidden()
    {
        var room = CreateRoom(_hostId);
        var kicked = CreateParticipant(_userId, isActive: false);
        kicked.LeftAt = DateTime.UtcNow;
        var translationRoomId = SetupRoomMessages(
            room,
            new[] { kicked },
            new[] { CreateMessage("said after the kick", DateTime.UtcNow) });
        _revocations.Add(new RtcSessionRevocation
        {
            MeetingRoomId = room.Id,
            InviteeUserId = _userId,
            Status = "REVOKED",
        });

        var result = await _sut.GetRoomMessagesAsync(translationRoomId, _userId);

        Assert.False(result.IsSuccess);
        Assert.Equal("FORBIDDEN", result.ErrorCode);
        _chatMessageRepoMock.Verify(m => m.FindAsync(
            It.IsAny<Expression<Func<MeetingChatMessage, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>A revocation for somebody else in the same room does not touch this reader.</summary>
    [Fact]
    public async Task GetRoomMessagesAsync_AnotherParticipantsRevocation_DoesNotBlockThisReader()
    {
        var room = CreateRoom(_hostId);
        var translationRoomId = SetupRoomMessages(
            room,
            new[] { CreateParticipant(_userId) },
            new[] { CreateMessage("hello", DateTime.UtcNow) });
        _revocations.Add(new RtcSessionRevocation
        {
            MeetingRoomId = room.Id,
            InviteeUserId = Guid.NewGuid(),
            Status = "REVOKED",
        });

        var result = await _sut.GetRoomMessagesAsync(translationRoomId, _userId);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task GetRoomMessagesAsync_RequesterNotParticipant_ReturnsForbidden()
    {
        var translationRoomId = SetupRoomMessages(
            CreateRoom(_hostId),
            new[] { CreateParticipant(Guid.NewGuid()) },
            new[] { CreateMessage("hello", DateTime.UtcNow) });

        var result = await _sut.GetRoomMessagesAsync(translationRoomId, _userId);

        Assert.False(result.IsSuccess);
        Assert.Equal("FORBIDDEN", result.ErrorCode);
        _chatMessageRepoMock.Verify(m => m.FindAsync(
            It.IsAny<Expression<Func<MeetingChatMessage, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetRoomMessagesAsync_HostAndHiddenMessage_HostSeesHiddenMessage()
    {
        var now = DateTime.UtcNow;
        var translationRoomId = SetupRoomMessages(
            CreateRoom(_hostId),
            Array.Empty<RtcStreamParticipant>(),
            new[] { CreateMessage("visible", now.AddMinutes(-2)), CreateMessage("moderated", now.AddMinutes(-1), isHidden: true) });

        var result = await _sut.GetRoomMessagesAsync(translationRoomId, _hostId);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "visible", "moderated" }, result.Value!.Select(m => m.OriginalText));
    }

    [Fact]
    public async Task GetRoomMessagesAsync_ParticipantAndHiddenMessage_HiddenMessageExcluded()
    {
        var now = DateTime.UtcNow;
        var translationRoomId = SetupRoomMessages(
            CreateRoom(_hostId),
            new[] { CreateParticipant(_userId) },
            new[] { CreateMessage("visible", now.AddMinutes(-2)), CreateMessage("moderated", now.AddMinutes(-1), isHidden: true) });

        var result = await _sut.GetRoomMessagesAsync(translationRoomId, _userId);

        Assert.True(result.IsSuccess);
        Assert.Equal("visible", Assert.Single(result.Value!).OriginalText);
    }

    [Fact]
    public async Task GetRoomMessagesAsync_ParticipantAndNoMessages_ReturnsEmptyList()
    {
        var translationRoomId = SetupRoomMessages(
            CreateRoom(_hostId),
            new[] { CreateParticipant(_userId) },
            Array.Empty<MeetingChatMessage>());

        var result = await _sut.GetRoomMessagesAsync(translationRoomId, _userId);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task GetRoomMessagesAsync_MessagesStoredOutOfOrder_ReturnsOrderedByCreatedAt()
    {
        var now = DateTime.UtcNow;
        var translationRoomId = SetupRoomMessages(
            CreateRoom(_hostId),
            Array.Empty<RtcStreamParticipant>(),
            new[]
            {
                CreateMessage("third", now),
                CreateMessage("first", now.AddMinutes(-10)),
                CreateMessage("second", now.AddMinutes(-5))
            });

        var result = await _sut.GetRoomMessagesAsync(translationRoomId, _hostId);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "first", "second", "third" }, result.Value!.Select(m => m.OriginalText));
    }
}
