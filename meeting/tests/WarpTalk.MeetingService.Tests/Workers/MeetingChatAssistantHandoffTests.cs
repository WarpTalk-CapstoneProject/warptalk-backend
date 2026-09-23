using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using WarpTalk.MeetingService.API.HostedServices;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Domain.Entities;
using WarpTalk.MeetingService.Domain.Interfaces;

namespace WarpTalk.MeetingService.Tests.Workers;

/// <summary>
/// "Chuyển qua widget để bàn tiếp" — the in-meeting WarpBot hands its thread to the widget.
///
/// The worker publishes a `handoff` result when the model calls continue_in_widget. This consumer
/// is the only hop between it and the browser, and it has dropped a result type in silence before
/// (tool_call_completed, for months). These pin the two things the client depends on: the event is
/// relayed at all, and it names the person who ASKED — from this service's own request row, not
/// from anything on the stream — so the one screen that should open its widget can tell it is the
/// one.
/// </summary>
public sealed class MeetingChatAssistantHandoffTests
{
    private readonly Guid _requestId = Guid.NewGuid();
    private readonly Guid _requesterId = Guid.NewGuid();
    private readonly Guid _meetingRoomId = Guid.NewGuid();
    private readonly Guid _translationRoomId = Guid.NewGuid();
    private readonly Mock<IMeetingChatNotifier> _notifier = new();
    private readonly Mock<IMeetingChatAssistantRequestRepository> _requests = new();

    private MeetingChatAssistantResultConsumerService BuildService(string requestStatus = "processing")
    {
        _requests
            .Setup(r => r.GetByIdAsync(_requestId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MeetingChatAssistantRequest
            {
                Id = _requestId,
                MeetingRoomId = _meetingRoomId,
                RequestedByUserId = _requesterId,
                Status = requestStatus,
                Prompt = "@WarpBot chuyển qua widget để bàn tiếp",
                ContextScope = "recent_messages",
                CreatedAt = DateTime.UtcNow,
            });

        var rooms = new Mock<IMeetingRoomRepository>();
        rooms
            .Setup(r => r.GetByIdAsync(_meetingRoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MeetingRoom
            {
                Id = _meetingRoomId,
                TranslationRoomId = _translationRoomId,
                ProviderRoomName = "room",
                Status = "ACTIVE",
                IsActive = true,
            });

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.MeetingChatAssistantRequestRepository).Returns(_requests.Object);
        unitOfWork.SetupGet(u => u.MeetingRoomRepository).Returns(rooms.Object);

        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IUnitOfWork))).Returns(unitOfWork.Object);
        provider.Setup(p => p.GetService(typeof(IMeetingChatNotifier))).Returns(_notifier.Object);

        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(provider.Object);
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        return new MeetingChatAssistantResultConsumerService(
            Mock.Of<IConnectionMultiplexer>(),
            scopeFactory.Object,
            Mock.Of<ILogger<MeetingChatAssistantResultConsumerService>>());
    }

    private Task ProcessAsync(MeetingChatAssistantResultConsumerService service, string type, string origin = "meeting_chat")
    {
        var entry = new StreamEntry("1-0", new[]
        {
            new NameValueEntry("request_id", _requestId.ToString()),
            new NameValueEntry("origin", origin),
            new NameValueEntry("type", type),
            // The worker's claim about the user. Deliberately a DIFFERENT id: the relay must
            // address the event from its own request row, never from the stream.
            new NameValueEntry("user_id", Guid.NewGuid().ToString()),
            new NameValueEntry("tool_name", "continue_in_widget"),
            new NameValueEntry("tool_calls_json", "{\"target\":\"widget\"}"),
        });

        var method = typeof(MeetingChatAssistantResultConsumerService).GetMethod(
            "ProcessEntryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)method.Invoke(service, new object[] { entry, CancellationToken.None })!;
    }

    [Fact]
    public async Task Handoff_IsRelayedToTheRoomGroup_AddressedToWhoeverAsked()
    {
        var service = BuildService();

        await ProcessAsync(service, "handoff");

        _notifier.Verify(
            n => n.BroadcastAssistantHandoffAsync(
                _translationRoomId,
                _requestId,
                _requesterId,
                "{\"target\":\"widget\"}",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handoff_DoesNotEndTheTurn()
    {
        // The answer that follows ("Opening it in the widget…") is still a WarpBot message in the
        // room, so the handoff must not be mistaken for the terminal event and persist nothing.
        var service = BuildService();

        await ProcessAsync(service, "handoff");

        _requests.Verify(r => r.Update(It.IsAny<MeetingChatAssistantRequest>()), Times.Never);
        _notifier.Verify(
            n => n.BroadcastMessageReceivedAsync(It.IsAny<Guid>(), It.IsAny<WarpTalk.MeetingService.Application.DTOs.MeetingChatMessageDto>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handoff_FromTheWidgetOrigin_IsNotThisConsumersToRelay()
    {
        var service = BuildService();

        await ProcessAsync(service, "handoff", origin: "assistant");

        _notifier.Verify(
            n => n.BroadcastAssistantHandoffAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
