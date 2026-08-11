using System.Text;
using System.Text.Json;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// Renders a SUMMARY_EXPORT artifact's stored JSON as the readable prose the knowledge index
/// should hold.
///
/// Shared because a meeting summary reaches the index from two places — ArtifactsFinalizer
/// when the meeting ends, SummaryResultConsumerWorker when someone re-summarises it under a
/// different template — and the two must produce byte-identical text. If they drifted, a
/// rewrite would silently change the embedding of a summary whose wording never changed.
/// </summary>
public static class MeetingSummaryKnowledgeText
{
    /// <summary>
    /// The summary as prose, or empty when there is nothing worth indexing.
    ///
    /// Empty for an insufficient-data summary: "the AI assistant could not generate a
    /// summary" is a status message, and indexing it as workspace knowledge would make the
    /// system claim to know something it does not. Empty is also what the caller must treat
    /// as "publish nothing".
    /// </summary>
    public static string Build(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return string.Empty;

            if (root.TryGetProperty("insufficientData", out var insufficient) &&
                insufficient.ValueKind == JsonValueKind.True)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();

            if (root.TryGetProperty("summary", out var summary) &&
                summary.ValueKind == JsonValueKind.String)
            {
                builder.AppendLine(summary.GetString());
            }

            AppendStringList(builder, root, "decisions", "Decisions:");

            if (root.TryGetProperty("actionItems", out var actionItems) &&
                actionItems.ValueKind == JsonValueKind.Array &&
                actionItems.GetArrayLength() > 0)
            {
                builder.AppendLine().AppendLine("Action items:");
                foreach (var item in actionItems.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var task = item.TryGetProperty("task", out var t) ? t.GetString() : null;
                    if (string.IsNullOrWhiteSpace(task)) continue;
                    var owner = item.TryGetProperty("owner", out var o) ? o.GetString() : null;
                    builder.AppendLine(
                        string.IsNullOrWhiteSpace(owner) ? $"- {task}" : $"- {owner}: {task}");
                }
            }

            return builder.ToString().Trim();
        }
        catch (JsonException)
        {
            // A summary that is not the structured shape is still readable text — an older
            // artifact, or one a fallback path wrote as markdown.
            return content.Trim();
        }
    }

    private static void AppendStringList(
        StringBuilder builder, JsonElement root, string property, string heading)
    {
        if (!root.TryGetProperty(property, out var list) ||
            list.ValueKind != JsonValueKind.Array ||
            list.GetArrayLength() == 0)
        {
            return;
        }

        builder.AppendLine().AppendLine(heading);
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                builder.AppendLine($"- {entry.GetString()}");
            }
        }
    }
}
