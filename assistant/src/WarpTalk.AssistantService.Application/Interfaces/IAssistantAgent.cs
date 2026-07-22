using System.Collections.Generic;
using System.Threading;

namespace WarpTalk.AssistantService.Application.Interfaces;

public abstract record AssistantAgentEvent;

public sealed record AssistantTextDelta(string Delta) : AssistantAgentEvent;

/// <summary>Emitted when the model asks to invoke a tool. Unused until Milestone B/C register tools.</summary>
public sealed record AssistantToolCallRequested(string CallId, string ToolName, string ArgumentsJson) : AssistantAgentEvent;

public sealed record AssistantCompleted(string FinalText) : AssistantAgentEvent;

public sealed record AssistantChatTurn(string Role, string Content);

public sealed record AssistantToolDefinition(string Name, string Description, string ParametersJsonSchema);

/// <summary>
/// Drives an OpenAI chat-completion loop, streaming deltas as they arrive. Implementations
/// call OpenAI directly over HTTP (see OpenAIAssistantAgent), matching the same lightweight,
/// no-SDK approach IChatTranslator already uses for message translation.
/// </summary>
public interface IAssistantAgent
{
    string ModelName { get; }
    int PromptVersion { get; }

    IAsyncEnumerable<AssistantAgentEvent> RunAsync(
        IReadOnlyList<AssistantChatTurn> history,
        IReadOnlyList<AssistantToolDefinition> availableTools,
        CancellationToken ct = default);
}
