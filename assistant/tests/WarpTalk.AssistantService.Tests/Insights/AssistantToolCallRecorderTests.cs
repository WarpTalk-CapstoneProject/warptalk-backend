using NSubstitute;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.AssistantService.Tests.Plugins;

namespace WarpTalk.AssistantService.Tests.Insights;

/// <summary>
/// Wave 4: the worker's tool_call_log becomes assistant_tool_calls rows - metadata only.
/// </summary>
public class AssistantToolCallRecorderTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ConversationId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly List<AssistantToolCall> _calls = [];
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    public AssistantToolCallRecorderTests()
    {
        var callRepository = InMemoryRepository.Create<IAssistantToolCallRepository, AssistantToolCall>(_calls, call => call.Id);
        _unitOfWork.AssistantToolCallRepository.Returns(callRepository);
        var conversations = new List<AssistantConversation>
        {
            new() { Id = ConversationId, WorkspaceId = WorkspaceId, UserId = UserId, Title = "t" },
        };
        var conversationRepository = InMemoryRepository.Create<IAssistantConversationRepository, AssistantConversation>(conversations, c => c.Id);
        _unitOfWork.AssistantConversationRepository.Returns(conversationRepository);
    }

    [Fact]
    public async Task MapsNewFormatEntries_WithoutStoringArgumentsOrResults()
    {
        var message = Message();
        const string json = """
            [
              {"tool":"google_calendar_create_event","arguments":"{\"title\":\"secret\"}","result":"{\"id\":\"x\"}","status":"failed",
               "source":"plugin","pluginKey":"google_calendar","outcome":"needs_setup","outcomeCode":"missing_scope",
               "durationMs":412.6,"startedAt":"2026-10-01T10:00:00.123Z"},
              {"tool":"web_search","arguments":"","result":"","status":"completed",
               "source":"web_search","pluginKey":null,"outcome":"ok","outcomeCode":null,"durationMs":null,"startedAt":"2026-10-01T10:00:01Z"}
            ]
            """;

        var written = await Sut().RecordAsync(message, json);

        Assert.Equal(2, written);
        var plugin = _calls.Single(c => c.ToolName == "google_calendar_create_event");
        Assert.Equal(message.Id, plugin.MessageId);
        Assert.Equal(WorkspaceId, plugin.WorkspaceId);
        Assert.Equal(UserId, plugin.UserId);
        Assert.Equal(AssistantToolCallConstants.Sources.Plugin, plugin.Source);
        Assert.Equal("google_calendar", plugin.PluginKey);
        Assert.Equal(AssistantToolCallConstants.Outcomes.NeedsSetup, plugin.Outcome);
        Assert.Equal("missing_scope", plugin.OutcomeCode);
        Assert.Equal("failed", plugin.Status);
        Assert.Equal(413, plugin.DurationMs);
        Assert.Equal(new DateTime(2026, 10, 1, 10, 0, 0, 123, DateTimeKind.Utc), plugin.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, plugin.CreatedAt.Kind);
        // Privacy: never the arguments, never the result.
        Assert.Equal(string.Empty, plugin.ArgumentsJson);
        Assert.Null(plugin.ResultJson);

        var search = _calls.Single(c => c.ToolName == "web_search");
        Assert.Equal(AssistantToolCallConstants.Sources.WebSearch, search.Source);
        Assert.Null(search.PluginKey);
        Assert.Equal(AssistantToolCallConstants.Outcomes.Ok, search.Outcome);
        Assert.Null(search.DurationMs);
        Assert.Null(search.CompletedAt);

        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MapsOldFormatEntries_AsBuiltinWithOutcomeFromStatus()
    {
        const string json = """
            [
              {"tool":"create_meeting","arguments":"{}","result":"{}","status":"completed"},
              {"tool":"search_transcripts","arguments":"{}","result":"{}","status":"failed"}
            ]
            """;

        await Sut().RecordAsync(Message(), json);

        var ok = _calls.Single(c => c.ToolName == "create_meeting");
        Assert.Equal(AssistantToolCallConstants.Sources.Builtin, ok.Source);
        Assert.Equal(AssistantToolCallConstants.Outcomes.Ok, ok.Outcome);
        Assert.Null(ok.PluginKey);
        Assert.Null(ok.DurationMs);
        Assert.Equal(Now.UtcDateTime, ok.CreatedAt);

        var failed = _calls.Single(c => c.ToolName == "search_transcripts");
        Assert.Equal(AssistantToolCallConstants.Sources.Builtin, failed.Source);
        Assert.Equal(AssistantToolCallConstants.Outcomes.Error, failed.Outcome);
    }

    [Fact]
    public async Task UnknownOutcome_IsAnError_AndEntriesWithoutATool_AreSkipped()
    {
        const string json = """
            [
              {"tool":"x","status":"completed","source":"builtin","outcome":"exploded"},
              {"arguments":"{}","status":"completed"},
              "not an object"
            ]
            """;

        var written = await Sut().RecordAsync(Message(), json);

        Assert.Equal(1, written);
        Assert.Equal(AssistantToolCallConstants.Outcomes.Error, Assert.Single(_calls).Outcome);
    }

    [Fact]
    public async Task ReFinalising_ReplacesTheMessagesRows()
    {
        var message = Message();
        var otherMessageRow = new AssistantToolCall
        {
            Id = Guid.NewGuid(), MessageId = Guid.NewGuid(), ToolName = "other", ArgumentsJson = "",
            Status = "completed", Source = "builtin", Outcome = "ok",
        };
        _calls.Add(otherMessageRow);

        await Sut().RecordAsync(message, """[{"tool":"a","status":"completed"},{"tool":"b","status":"completed"}]""");
        await Sut().RecordAsync(message, """[{"tool":"c","status":"completed"}]""");

        var mine = _calls.Where(c => c.MessageId == message.Id).ToList();
        Assert.Equal("c", Assert.Single(mine).ToolName);
        Assert.Contains(otherMessageRow, _calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"tool\":\"a\"}")]
    public async Task APayloadThatIsNotAnArray_WritesNothing_AndKeepsExistingRows(string json)
    {
        var message = Message();
        await Sut().RecordAsync(message, """[{"tool":"a","status":"completed"}]""");

        var written = await Sut().RecordAsync(message, json);

        Assert.Equal(0, written);
        Assert.Single(_calls);
    }

    private AssistantToolCallRecorder Sut() => new(_unitOfWork, new FixedTimeProvider(Now));

    private static AssistantMessage Message() => new()
    {
        Id = Guid.NewGuid(),
        ConversationId = ConversationId,
        WorkspaceId = WorkspaceId,
        Role = "assistant",
        Content = "",
        Status = "completed",
    };
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
