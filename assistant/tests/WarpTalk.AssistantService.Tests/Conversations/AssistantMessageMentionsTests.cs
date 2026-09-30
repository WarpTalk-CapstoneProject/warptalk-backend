using System.Linq.Expressions;
using System.Text.Json;
using NSubstitute;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Mappers;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;

namespace WarpTalk.AssistantService.Tests.Conversations;

/// <summary>
/// The @mentions a user message was sent with are kept on that message, so a conversation reopened
/// from history can still show what the user pointed WarpBot at.
/// </summary>
/// <remarks>
/// The property that matters is not "something was stored" but "the SAME string was stored as the
/// worker received". Two serializations would be two chances to disagree about what was mentioned,
/// and the thread would then show one thing while the answer was built from another.
/// </remarks>
public class AssistantMessageMentionsTests
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly AssistantConversation _conversation;
    private readonly List<AssistantMessage> _added = [];
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAssistantChatRequestPublisher _publisher = Substitute.For<IAssistantChatRequestPublisher>();
    private readonly AssistantConversationService _service;

    public AssistantMessageMentionsTests()
    {
        _conversation = new AssistantConversation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _workspaceId,
            UserId = _userId,
            Title = "New chat",
            CreatedAt = DateTime.UtcNow,
        };

        var conversations = Substitute.For<IAssistantConversationRepository>();
        conversations.GetByIdAsync(_conversation.Id, Arg.Any<CancellationToken>()).Returns(_conversation);
        _unitOfWork.AssistantConversationRepository.Returns(conversations);

        var messages = Substitute.For<IAssistantMessageRepository>();
        messages.AddAsync(Arg.Do<AssistantMessage>(_added.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        messages
            .FindAsync(Arg.Any<Expression<Func<AssistantMessage, bool>>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<AssistantMessage>());
        _unitOfWork.AssistantMessageRepository.Returns(messages);

        _service = new AssistantConversationService(_unitOfWork, _publisher);
    }

    private string? PublishedMentionsJson() =>
        (string?)_publisher.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IAssistantChatRequestPublisher.PublishAsync))
            .GetArguments()[7];

    [Fact]
    public async Task TheUserMessageStoresTheExactMentionsTheWorkerReceived()
    {
        var request = new SendAssistantMessageRequest(
            "set up a meeting for me",
            Mentions: [new AssistantMentionDto("plugin", "google_meet", "Google Meet")]);

        var result = await _service.SendMessageAsync(_conversation.Id, _userId, null, request);

        Assert.True(result.IsSuccess);
        var userMessage = Assert.Single(_added, message => message.Role == "user");
        Assert.NotNull(userMessage.MentionsJson);
        Assert.Equal(PublishedMentionsJson(), userMessage.MentionsJson);

        using var stored = JsonDocument.Parse(userMessage.MentionsJson!);
        var mention = Assert.Single(stored.RootElement.EnumerateArray());
        Assert.Equal("plugin", mention.GetProperty("entityType").GetString());
        Assert.Equal("google_meet", mention.GetProperty("entityId").GetString());
        Assert.Equal("Google Meet", mention.GetProperty("label").GetString());
        // Stamped by the server from the conversation, never taken from the client.
        Assert.Equal(_workspaceId.ToString(), mention.GetProperty("workspaceId").GetString());
    }

    [Fact]
    public async Task AMessageThatNamesNothingStoresNull()
    {
        var result = await _service.SendMessageAsync(
            _conversation.Id, _userId, null, new SendAssistantMessageRequest("hello"));

        Assert.True(result.IsSuccess);
        Assert.Null(Assert.Single(_added, message => message.Role == "user").MentionsJson);
    }

    [Fact]
    public async Task MentionsAreNeverWrittenOntoTheAnswerPlaceholder()
    {
        var request = new SendAssistantMessageRequest(
            "what's in my drive",
            Mentions: [new AssistantMentionDto("plugin", "google_drive", "Google Drive")]);

        await _service.SendMessageAsync(_conversation.Id, _userId, null, request);

        Assert.Null(Assert.Single(_added, message => message.Role == "assistant").MentionsJson);
    }

    [Fact]
    public void TheMessageDtoCarriesTheStoredMentions()
    {
        const string json = """[{"entityType":"plugin","entityId":"google_meet","label":"Google Meet","workspaceId":"w"}]""";
        var dto = new AssistantMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = Guid.NewGuid(),
            Role = "user",
            Content = "x",
            Status = "completed",
            MentionsJson = json,
        }.ToDto();

        Assert.Equal(json, dto.MentionsJson);
    }
}
