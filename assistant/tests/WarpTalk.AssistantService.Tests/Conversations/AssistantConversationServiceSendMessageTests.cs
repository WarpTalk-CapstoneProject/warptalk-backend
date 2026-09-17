using System.Linq.Expressions;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WarpTalk.AssistantService.API.Controllers;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;

namespace WarpTalk.AssistantService.Tests.Conversations;

/// <summary>
/// Function 52 - Ask WarpBot Assistant: AssistantConversationService.SendMessageAsync, reached
/// through AssistantConversationsController.SendMessage.
/// </summary>
public class AssistantConversationServiceSendMessageTests
{
    private const string PngDataUrl = "data:image/png;base64,iVBORw0KGgo=";

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _otherUserId = Guid.NewGuid();
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly AssistantConversation _conversation;
    private readonly List<AssistantMessage> _added = [];
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAssistantConversationRepository _conversations = Substitute.For<IAssistantConversationRepository>();
    private readonly IAssistantMessageRepository _messages = Substitute.For<IAssistantMessageRepository>();
    private readonly IAssistantChatRequestPublisher _publisher = Substitute.For<IAssistantChatRequestPublisher>();
    private readonly AssistantConversationService _service;

    public AssistantConversationServiceSendMessageTests()
    {
        _conversation = new AssistantConversation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _workspaceId,
            UserId = _userId,
            Title = "New chat",
            CreatedAt = DateTime.UtcNow.AddHours(-1),
        };

        _conversations.GetByIdAsync(_conversation.Id, Arg.Any<CancellationToken>()).Returns(_conversation);
        _unitOfWork.AssistantConversationRepository.Returns(_conversations);

        _messages.AddAsync(Arg.Do<AssistantMessage>(_added.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _messages
            .FindAsync(Arg.Any<Expression<Func<AssistantMessage, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<AssistantMessage>());
        _unitOfWork.AssistantMessageRepository.Returns(_messages);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(1);

        _service = new AssistantConversationService(_unitOfWork, _publisher);
    }

    private object?[] PublishedArguments() =>
        _publisher.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IAssistantChatRequestPublisher.PublishAsync))
            .GetArguments();

    private IReadOnlyList<ChatTurnDto> PublishedHistory() => (IReadOnlyList<ChatTurnDto>)PublishedArguments()[5]!;

    [Fact]
    public async Task UTCID01_OwnConversation_ControllerReturns202_PersistsUserAndPendingAssistantMessage_AndPublishes()
    {
        var controller = new AssistantConversationsController(_service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, _userId.ToString())], "test")),
                },
            },
        };
        controller.Request.Headers.Authorization = "Bearer token-1";

        var actionResult = await controller.SendMessage(
            _conversation.Id, new SendAssistantMessageRequest("What did we decide?"), CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(actionResult);
        Assert.Equal(StatusCodes.Status202Accepted, accepted.StatusCode);
        var response = Assert.IsType<SendAssistantMessageResponse>(accepted.Value);

        var userMessage = Assert.Single(_added, m => m.Role == "user");
        Assert.Equal(response.MessageId, userMessage.Id);
        Assert.Equal("What did we decide?", userMessage.Content);
        Assert.Equal("completed", userMessage.Status);
        Assert.Equal(_userId, userMessage.UserId);
        Assert.Equal(_workspaceId, userMessage.WorkspaceId);
        Assert.Equal(_conversation.Id, userMessage.ConversationId);

        var assistantMessage = Assert.Single(_added, m => m.Role == "assistant");
        Assert.Equal(response.AssistantMessageId, assistantMessage.Id);
        Assert.Equal("pending", assistantMessage.Status);
        Assert.Equal("", assistantMessage.Content);
        Assert.Null(assistantMessage.UserId);
        Assert.Null(assistantMessage.CompletedAt);

        Assert.Equal("What did we decide?", _conversation.Title);
        Assert.Equal(userMessage.CreatedAt, _conversation.LastMessageAt);
        _conversations.Received(1).Update(_conversation);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());

        await _publisher.Received(1).PublishAsync(
            assistantMessage.Id, _conversation.Id, _workspaceId, _userId, "Bearer token-1",
            Arg.Any<IReadOnlyList<ChatTurnDto>>(), null, null, null, null, Arg.Any<CancellationToken>());
        var turn = Assert.Single(PublishedHistory());
        Assert.Equal(new ChatTurnDto("user", "What did we decide?"), turn);
    }

    [Fact]
    public async Task UTCID02_ConversationOwnedByAnotherUser_ReturnsNotFound_AndPersistsNothing()
    {
        _conversation.UserId = _otherUserId;

        var result = await _service.SendMessageAsync(
            _conversation.Id, _userId, null, new SendAssistantMessageRequest("What did we decide?"));

        Assert.False(result.IsSuccess);
        Assert.Equal("NOT_FOUND", result.ErrorCode);
        Assert.Equal("Conversation not found.", result.Error);
        Assert.Empty(_added);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Empty(_publisher.ReceivedCalls());
    }

    [Fact]
    public async Task UTCID03_ConversationNotFound_ReturnsNotFound_AndPersistsNothing()
    {
        var missingId = Guid.NewGuid();
        _conversations.GetByIdAsync(missingId, Arg.Any<CancellationToken>()).Returns((AssistantConversation?)null);

        var result = await _service.SendMessageAsync(
            missingId, _userId, null, new SendAssistantMessageRequest("What did we decide?"));

        Assert.False(result.IsSuccess);
        Assert.Equal("NOT_FOUND", result.ErrorCode);
        Assert.Empty(_added);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Empty(_publisher.ReceivedCalls());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UTCID04_BlankContentWithoutAttachment_ReturnsValidationError_BeforeConversationLookup(string content)
    {
        var result = await _service.SendMessageAsync(
            _conversation.Id, _userId, null, new SendAssistantMessageRequest(content, Attachments: []));

        Assert.False(result.IsSuccess);
        Assert.Equal("VALIDATION_ERROR", result.ErrorCode);
        Assert.Equal("Message content is required.", result.Error);
        await _conversations.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        Assert.Empty(_added);
        Assert.Empty(_publisher.ReceivedCalls());
    }

    [Fact]
    public async Task UTCID05_ContentLongerThan60Chars_OnNewChat_TruncatesTitleTo60()
    {
        var content = new string('a', 50) + new string('b', 30);

        var result = await _service.SendMessageAsync(
            _conversation.Id, _userId, null, new SendAssistantMessageRequest(content));

        Assert.True(result.IsSuccess);
        Assert.Equal(60, _conversation.Title.Length);
        Assert.Equal(content[..60], _conversation.Title);
        // Only the title is truncated; the stored message keeps the full content.
        Assert.Equal(content, Assert.Single(_added, m => m.Role == "user").Content);
    }

    [Fact]
    public async Task UTCID06_AttachmentOnlyMessage_Succeeds_KeepsNewChatTitle_AndPublishesAttachments()
    {
        var request = new SendAssistantMessageRequest(
            "", Attachments: [new AssistantAttachmentDto(PngDataUrl, "shot.png", "image/png")]);

        var result = await _service.SendMessageAsync(_conversation.Id, _userId, null, request);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, _added.Count);
        Assert.Equal("", Assert.Single(_added, m => m.Role == "user").Content);
        Assert.Equal("New chat", _conversation.Title);

        var attachmentsJson = (string?)PublishedArguments()[8];
        Assert.NotNull(attachmentsJson);
        Assert.Contains(PngDataUrl, attachmentsJson);
        Assert.Contains("shot.png", attachmentsJson);
    }

    [Fact]
    public async Task UTCID07_PriorCompletedMessages_AreInHistoryInCreatedOrder_BeforeCurrentUserMessage()
    {
        _conversation.Title = "Planning";
        var t0 = DateTime.UtcNow.AddMinutes(-10);
        // Returned out of order on purpose: the service orders by CreatedAt.
        _messages
            .FindAsync(Arg.Any<Expression<Func<AssistantMessage, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new AssistantMessage { Id = Guid.NewGuid(), ConversationId = _conversation.Id, Role = "assistant", Content = "Ship Friday.", Status = "completed", CreatedAt = t0.AddMinutes(1) },
                new AssistantMessage { Id = Guid.NewGuid(), ConversationId = _conversation.Id, Role = "user", Content = "When do we ship?", Status = "completed", CreatedAt = t0 },
            });

        var result = await _service.SendMessageAsync(
            _conversation.Id, _userId, null, new SendAssistantMessageRequest("What did we decide?"));

        Assert.True(result.IsSuccess);
        Assert.Equal("Planning", _conversation.Title);
        Assert.Equal(
            [
                new ChatTurnDto("user", "When do we ship?"),
                new ChatTurnDto("assistant", "Ship Friday."),
                new ChatTurnDto("user", "What did we decide?"),
            ],
            PublishedHistory());

        // The prior-message filter: completed messages of this conversation, excluding the new user message.
        var predicate = (Expression<Func<AssistantMessage, bool>>)_messages.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IAssistantMessageRepository.FindAsync))
            .GetArguments()[0]!;
        var filter = predicate.Compile();
        var userMessage = Assert.Single(_added, m => m.Role == "user");
        var pending = Assert.Single(_added, m => m.Role == "assistant");
        Assert.False(filter(userMessage));
        Assert.False(filter(pending));
        Assert.True(filter(new AssistantMessage { Id = Guid.NewGuid(), ConversationId = _conversation.Id, Status = "completed" }));
        Assert.False(filter(new AssistantMessage { Id = Guid.NewGuid(), ConversationId = Guid.NewGuid(), Status = "completed" }));
    }

    [Fact]
    public async Task UTCID08_PublisherThrows_ExceptionPropagates_AfterMessagesWereCommitted()
    {
        _publisher
            .PublishAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>(),
                Arg.Any<IReadOnlyList<ChatTurnDto>>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("redis down"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SendMessageAsync(
            _conversation.Id, _userId, null, new SendAssistantMessageRequest("What did we decide?")));

        Assert.Equal("redis down", ex.Message);
        // No catch/compensation: the user message and the pending placeholder were already saved.
        Assert.Equal(2, _added.Count);
        Assert.Equal("pending", Assert.Single(_added, m => m.Role == "assistant").Status);
        Received.InOrder(() =>
        {
            _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>());
            _publisher.PublishAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>(),
                Arg.Any<IReadOnlyList<ChatTurnDto>>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>());
        });
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
