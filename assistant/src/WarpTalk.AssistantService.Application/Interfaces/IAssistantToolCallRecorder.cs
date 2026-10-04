using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// Writes one <c>assistant_tool_calls</c> row per entry of a finished workspace answer's
/// <c>tool_calls_json</c> (wave 4). Metadata only: no argument or result text is stored.
/// </summary>
public interface IAssistantToolCallRecorder
{
    /// <summary>
    /// Replaces the message's recorded calls with the entries in <paramref name="toolCallsJson"/>
    /// and saves. Returns the number of rows written; a payload that does not parse as a JSON array
    /// writes nothing and leaves existing rows alone.
    /// </summary>
    Task<int> RecordAsync(AssistantMessage message, string toolCallsJson, CancellationToken ct = default);
}
