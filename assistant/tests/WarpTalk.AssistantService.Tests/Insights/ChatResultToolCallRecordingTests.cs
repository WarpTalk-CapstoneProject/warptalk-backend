using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using WarpTalk.AssistantService.API.Services;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.AssistantService.Tests.Plugins;
using WarpTalk.Shared.Coordination;

namespace WarpTalk.AssistantService.Tests.Insights;

/// <summary>
/// Wave 4: where AssistantChatResultConsumerService records tool calls - workspace answers only, and
/// never at the cost of the answer.
/// </summary>
public class ChatResultToolCallRecordingTests
{
    private const string ToolCallsJson = """[{"tool":"create_meeting","arguments":"{}","result":"{}","status":"completed"}]""";

    private static readonly Guid ConversationId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly List<AssistantMessage> _messages = [];
    private readonly List<PlatformMessage> _platformMessages = [];
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAssistantNotifier _notifier = Substitute.For<IAssistantNotifier>();
    private readonly IAssistantToolCallRecorder _recorder = Substitute.For<IAssistantToolCallRecorder>();

    public ChatResultToolCallRecordingTests()
    {
        var messages = InMemoryRepository.Create<IAssistantMessageRepository, AssistantMessage>(_messages, m => m.Id);
        var platformMessages = InMemoryRepository.Create<IPlatformMessageRepository, PlatformMessage>(_platformMessages, m => m.Id);
        _unitOfWork.AssistantMessageRepository.Returns(messages);
        _unitOfWork.PlatformMessageRepository.Returns(platformMessages);
    }

    [Fact]
    public async Task AWorkspaceAnswer_RecordsItsToolCalls_AfterTheMessageIsSaved()
    {
        var message = WorkspaceMessage();

        await Finalize(message.Id, ToolCallsJson, AssistantConversationScopes.Workspace);

        Received.InOrder(() =>
        {
            _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>());
            _recorder.RecordAsync(message, ToolCallsJson, Arg.Any<CancellationToken>());
        });
        Assert.Equal("completed", message.Status);
    }

    [Fact]
    public async Task APlatformAnswer_RecordsNothing()
    {
        var message = new PlatformMessage
        {
            Id = Guid.NewGuid(), ConversationId = ConversationId, Role = "assistant", Content = "", Status = "streaming",
        };
        _platformMessages.Add(message);

        await Finalize(message.Id, ToolCallsJson, AssistantConversationScopes.Platform);

        Assert.Equal("completed", message.Status);
        await _recorder.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default);
    }

    [Fact]
    public async Task APlatformAnswerFromAWorkerWithoutScope_RecordsNothing()
    {
        var message = new PlatformMessage
        {
            Id = Guid.NewGuid(), ConversationId = ConversationId, Role = "assistant", Content = "", Status = "streaming",
        };
        _platformMessages.Add(message);

        await Finalize(message.Id, ToolCallsJson, resultScope: "");

        await _recorder.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default);
    }

    [Fact]
    public async Task ARecordingFailure_DoesNotBreakTheMessage()
    {
        var message = WorkspaceMessage();
        _recorder.RecordAsync(Arg.Any<AssistantMessage>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database down"));

        var exception = await Record.ExceptionAsync(() => Finalize(message.Id, ToolCallsJson, AssistantConversationScopes.Workspace));

        Assert.Null(exception);
        Assert.Equal("completed", message.Status);
        Assert.Equal("answer", message.Content);
        await _notifier.Received(1).BroadcastMessageCompletedAsync(ConversationId, Arg.Any<AssistantMessageDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnAnswerWithoutToolCalls_RecordsNothing()
    {
        var message = WorkspaceMessage();

        await Finalize(message.Id, toolCallsJson: "", AssistantConversationScopes.Workspace);

        await _recorder.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default);
    }

    private AssistantMessage WorkspaceMessage()
    {
        var message = new AssistantMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = ConversationId,
            WorkspaceId = Guid.NewGuid(),
            Role = "assistant",
            Content = "",
            Status = "streaming",
        };
        _messages.Add(message);
        return message;
    }

    private async Task Finalize(Guid messageId, string toolCallsJson, string resultScope)
    {
        var services = new ServiceCollection()
            .AddSingleton(_unitOfWork)
            .AddSingleton(_notifier)
            .AddSingleton(_recorder)
            .BuildServiceProvider();
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(services);

        var consumer = new AssistantChatResultConsumerService(
            Substitute.For<IConnectionMultiplexer>(),
            Substitute.For<IServiceScopeFactory>(),
            NullLogger<AssistantChatResultConsumerService>.Instance,
            Substitute.For<ILeaderElection>());

        await consumer.FinalizeMessageAsync(
            scope, ConversationId, messageId, "answer", toolCallsJson, "", failed: false, resultScope, CancellationToken.None);
    }
}
