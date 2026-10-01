using System.Globalization;
using System.Text.Json;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;

namespace WarpTalk.AssistantService.Application.Services;

/// <summary>
/// Turns the worker's <c>tool_call_log</c> (warptalk-ai <c>chat_worker.py</c>) into
/// <c>assistant_tool_calls</c> rows.
/// </summary>
/// <remarks>
/// PRIVACY. An entry carries the call's <c>arguments</c> and <c>result</c> for the UI, which keeps them
/// in <c>assistant_messages.tool_results_json</c>. Neither is copied here: <c>arguments_json</c> is
/// always empty and <c>result_json</c> always null.
/// <para>
/// OLD ENTRIES. A worker from before wave 4 sends only <c>tool</c>, <c>arguments</c>, <c>result</c> and
/// <c>status</c>. Such an entry is a built-in call (no <c>pluginKey</c>), <c>ok</c> when its status is
/// <c>completed</c> and <c>error</c> otherwise, started "now".
/// </para>
/// </remarks>
public class AssistantToolCallRecorder : IAssistantToolCallRecorder
{
    private static readonly HashSet<string> KnownSources = new(StringComparer.Ordinal)
    {
        AssistantToolCallConstants.Sources.Builtin,
        AssistantToolCallConstants.Sources.Plugin,
        AssistantToolCallConstants.Sources.WebSearch,
    };

    private static readonly HashSet<string> KnownOutcomes = new(StringComparer.Ordinal)
    {
        AssistantToolCallConstants.Outcomes.Ok,
        AssistantToolCallConstants.Outcomes.Error,
        AssistantToolCallConstants.Outcomes.Blocked,
        AssistantToolCallConstants.Outcomes.NeedsSetup,
        AssistantToolCallConstants.Outcomes.Declined,
        AssistantToolCallConstants.Outcomes.ConfirmationRequired,
    };

    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public AssistantToolCallRecorder(IUnitOfWork unitOfWork, TimeProvider? timeProvider = null)
    {
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<int> RecordAsync(AssistantMessage message, string toolCallsJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(toolCallsJson))
            return 0;

        List<JsonElement> entries;
        try
        {
            using var document = JsonDocument.Parse(toolCallsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return 0;
            entries = document.RootElement.EnumerateArray()
                .Where(entry => entry.ValueKind == JsonValueKind.Object)
                .Select(entry => entry.Clone())
                .ToList();
        }
        catch (JsonException)
        {
            return 0;
        }

        // Who made the calls is the conversation's, not the assistant message's (whose user_id is
        // null on an assistant reply). The message's own columns are the fallback.
        var conversation = await _unitOfWork.AssistantConversationRepository.GetByIdAsync(message.ConversationId, ct);
        var workspaceId = conversation?.WorkspaceId ?? message.WorkspaceId;
        var userId = conversation?.UserId ?? message.UserId;

        // A retried answer is finalised again: its calls replace the first attempt's rather than
        // counting twice.
        var existing = await _unitOfWork.AssistantToolCallRepository.FindAsync(call => call.MessageId == message.Id, ct: ct);
        foreach (var row in existing)
            _unitOfWork.AssistantToolCallRepository.Remove(row);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var written = 0;
        foreach (var entry in entries)
        {
            var row = ToRow(entry, message.Id, workspaceId, userId, now);
            if (row == null)
                continue;
            await _unitOfWork.AssistantToolCallRepository.AddAsync(row, ct);
            written++;
        }

        await _unitOfWork.SaveChangesAsync(ct);
        return written;
    }

    /// <summary>One entry as a row, or null for an entry that names no tool.</summary>
    public static AssistantToolCall? ToRow(JsonElement entry, Guid messageId, Guid workspaceId, Guid? userId, DateTime nowUtc)
    {
        var tool = ReadString(entry, "tool");
        if (string.IsNullOrWhiteSpace(tool))
            return null;

        var status = ReadString(entry, "status") ?? AssistantToolCallConstants.CompletedStatus;
        var pluginKey = ReadString(entry, "pluginKey");
        if (string.IsNullOrWhiteSpace(pluginKey))
            pluginKey = null;

        var source = ReadString(entry, "source");
        if (source == null || !KnownSources.Contains(source))
            source = pluginKey != null ? AssistantToolCallConstants.Sources.Plugin : AssistantToolCallConstants.Sources.Builtin;

        var completed = string.Equals(status, AssistantToolCallConstants.CompletedStatus, StringComparison.OrdinalIgnoreCase);
        var outcome = ReadString(entry, "outcome");
        if (outcome == null)
            outcome = completed ? AssistantToolCallConstants.Outcomes.Ok : AssistantToolCallConstants.Outcomes.Error;
        else if (!KnownOutcomes.Contains(outcome))
            outcome = AssistantToolCallConstants.Outcomes.Error;

        var outcomeCode = ReadString(entry, "outcomeCode");
        if (string.IsNullOrWhiteSpace(outcomeCode))
            outcomeCode = null;

        var durationMs = ReadDuration(entry);
        var startedAt = ReadStartedAt(entry) ?? nowUtc;

        return new AssistantToolCall
        {
            Id = Guid.NewGuid(),
            MessageId = messageId,
            WorkspaceId = workspaceId,
            UserId = userId,
            ToolName = Truncate(tool, AssistantToolCallConstants.ToolNameMaxLength),
            // Never the arguments or the result: metadata only.
            ArgumentsJson = string.Empty,
            ResultJson = null,
            Status = Truncate(status, AssistantToolCallConstants.StatusMaxLength),
            Source = source,
            PluginKey = pluginKey == null ? null : Truncate(pluginKey, AssistantToolCallConstants.PluginKeyMaxLength),
            Outcome = outcome,
            OutcomeCode = outcomeCode == null ? null : Truncate(outcomeCode, AssistantToolCallConstants.OutcomeCodeMaxLength),
            DurationMs = durationMs,
            CreatedAt = startedAt,
            CompletedAt = durationMs is { } ms ? startedAt.AddMilliseconds(ms) : null,
        };
    }

    private static string? ReadString(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadDuration(JsonElement entry)
    {
        if (!entry.TryGetProperty("durationMs", out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        if (!value.TryGetDouble(out var ms) || double.IsNaN(ms) || ms < 0)
            return null;
        return ms >= int.MaxValue ? int.MaxValue : (int)Math.Round(ms);
    }

    private static DateTime? ReadStartedAt(JsonElement entry)
    {
        var raw = ReadString(entry, "startedAt");
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.UtcDateTime
            : null;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
