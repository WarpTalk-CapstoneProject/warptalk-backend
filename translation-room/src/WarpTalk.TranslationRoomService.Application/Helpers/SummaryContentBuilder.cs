using System;
using System.Collections.Generic;
using System.Text.Json;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// The JSON shape the meeting page reads a summary from.
///
/// Extracted from ArtifactsFinalizer for WT-379: the late-summary recovery in
/// ArtifactsReconciliationWorker has to produce byte-identical content to the finalizer, because
/// a recovered summary and a first-try summary are the same artifact seen at two different times.
/// Two copies of this would drift, and the drift would only ever show on the recovery path —
/// the one nobody looks at.
/// </summary>
public static class SummaryContentBuilder
{
    public static string Build(string? structuredJson, string? summaryContent, string? actionItemsRaw)
    {
        if (!string.IsNullOrWhiteSpace(structuredJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(structuredJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("summary", out _))
                {
                    // Already in the shape the frontend expects — pass through verbatim.
                    return structuredJson;
                }
            }
            catch (JsonException)
            {
                // Fall through to the best-effort text-based reconstruction below.
            }
        }

        if (string.IsNullOrWhiteSpace(summaryContent) && string.IsNullOrWhiteSpace(actionItemsRaw))
        {
            return JsonSerializer.Serialize(new
            {
                summary = "The AI assistant could not generate a summary for this meeting (no transcript content was available or generation did not complete in time).",
                decisions = Array.Empty<string>(),
                actionItems = Array.Empty<object>(),
                insufficientData = true
            });
        }

        return JsonSerializer.Serialize(new
        {
            summary = summaryContent ?? string.Empty,
            decisions = Array.Empty<string>(),
            actionItems = ParseActionItemsMarkdown(actionItemsRaw),
            insufficientData = false
        });
    }

    /// <summary>
    /// Best-effort parse of MeetingAssistant.extract_action_items's plain-text output
    /// (format: "[ ] Action item - @assignee") into {owner, task} pairs.
    /// </summary>
    private static List<object> ParseActionItemsMarkdown(string? actionItemsRaw)
    {
        var result = new List<object>();
        if (string.IsNullOrWhiteSpace(actionItemsRaw)) return result;

        var lines = actionItemsRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimStart('-', '*', ' ');
            if (line.StartsWith("[ ]") || line.StartsWith("[x]", StringComparison.OrdinalIgnoreCase))
            {
                line = line.Substring(3).Trim();
            }
            if (string.IsNullOrWhiteSpace(line)) continue;

            var atIndex = line.LastIndexOf(" - @", StringComparison.Ordinal);
            if (atIndex >= 0)
            {
                var task = line.Substring(0, atIndex).Trim();
                var owner = line.Substring(atIndex + 4).Trim();
                result.Add(new { owner, task });
            }
            else
            {
                result.Add(new { owner = "", task = line });
            }
        }

        return result;
    }

}
