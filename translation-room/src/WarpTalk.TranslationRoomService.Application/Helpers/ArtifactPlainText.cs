using System;
using System.Linq;
using System.Text;
using System.Text.Json;

using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// What a text artifact looks like when somebody downloads it.
///
/// WHY THE STORED BYTES AND THE DOWNLOADED FILE ARE DIFFERENT THINGS
///   The transcript is stored as markdown and the summary as structured JSON, and both of those
///   are correct for what reads them: the web client parses the summary JSON into prose
///   (parseMeetingSummaryContent) and renders the transcript's markdown, and the knowledge indexer
///   reads the same JSON. But a person clicking Download does not want either. They wanted the
///   transcript, and they got `**[Nam (VI)]**: xin chào`; they wanted the summary, and they got
///   `{"summary":"…","decisions":[…]}`.
///
///   So the storage shape is left exactly as it is and this renders it on the way out. That also
///   fixes every artifact ALREADY in the database, which changing the writer would not: the rows
///   are immutable once finalized.
///
/// KEEP IN STEP WITH MeetingSummaryKnowledgeText
///   That helper renders the same JSON for the knowledge index and deliberately returns EMPTY for
///   an insufficient-data summary — indexing "the assistant could not summarise this" would make
///   the workspace claim to know something it does not. A download has the opposite duty: an empty
///   file is indistinguishable from a broken one, so the same case is written out as a sentence.
/// </summary>
public static class ArtifactPlainText
{
    /// <summary>
    /// True for the two artifacts that are text a person reads, rather than a file they open in
    /// something else. These are the ones served as .txt.
    /// </summary>
    public static bool IsTextExport(string? artifactType) =>
        string.Equals(artifactType, ArtifactType.TRANSCRIPT_EXPORT.ToString(), StringComparison.OrdinalIgnoreCase)
        || string.Equals(artifactType, ArtifactType.SUMMARY_EXPORT.ToString(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The artifact's stored content as plain text, or the content unchanged when this is not a
    /// text export (a recording has no text to render and must not be touched).
    /// </summary>
    public static string? Render(string? artifactType, string? content)
    {
        if (string.IsNullOrWhiteSpace(content) || !IsTextExport(artifactType)) return content;

        return string.Equals(artifactType, ArtifactType.SUMMARY_EXPORT.ToString(), StringComparison.OrdinalIgnoreCase)
            ? RenderSummary(content)
            : RenderTranscript(content);
    }

    /// <summary>
    /// The summary's JSON as a readable note.
    ///
    /// Falls back to the content verbatim when it is not the structured shape: older artifacts and
    /// one fallback path store prose, and prose is already what this method is trying to produce.
    /// </summary>
    private static string RenderSummary(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return content.Trim();

            if (root.TryGetProperty("insufficientData", out var insufficient)
                && insufficient.ValueKind == JsonValueKind.True)
            {
                return "Meeting summary\n\nThere was not enough of this meeting recorded to summarise it.";
            }

            var builder = new StringBuilder();
            builder.AppendLine("Meeting summary").AppendLine();

            if (root.TryGetProperty("summary", out var summary)
                && summary.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(summary.GetString()))
            {
                builder.AppendLine(summary.GetString()!.Trim());
            }

            AppendStringList(builder, root, "keyPoints", "Key points");
            AppendStringList(builder, root, "decisions", "Decisions");
            AppendActionItems(builder, root);

            var rendered = builder.ToString().Trim();

            // A structured payload with nothing in it is still a document that has to say
            // something. Empty would download as a 0-byte file that reads as a failure.
            return rendered.Length > "Meeting summary".Length
                ? rendered
                : "Meeting summary\n\nNo summary content was stored for this meeting.";
        }
        catch (JsonException)
        {
            return content.Trim();
        }
    }

    private static void AppendStringList(StringBuilder builder, JsonElement root, string property, string heading)
    {
        if (!root.TryGetProperty(property, out var list)
            || list.ValueKind != JsonValueKind.Array
            || list.GetArrayLength() == 0)
        {
            return;
        }

        var entries = list.EnumerateArray()
            .Where(entry => entry.ValueKind == JsonValueKind.String)
            .Select(entry => entry.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        if (entries.Count == 0) return;

        builder.AppendLine().AppendLine($"{heading}:");
        foreach (var entry in entries)
        {
            builder.AppendLine($"- {entry!.Trim()}");
        }
    }

    private static void AppendActionItems(StringBuilder builder, JsonElement root)
    {
        if (!root.TryGetProperty("actionItems", out var actionItems)
            || actionItems.ValueKind != JsonValueKind.Array
            || actionItems.GetArrayLength() == 0)
        {
            return;
        }

        var lines = new StringBuilder();
        foreach (var item in actionItems.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var task = item.TryGetProperty("task", out var taskValue) ? taskValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(task)) continue;

            var owner = item.TryGetProperty("owner", out var ownerValue) ? ownerValue.GetString() : null;
            lines.AppendLine(string.IsNullOrWhiteSpace(owner)
                ? $"- {task.Trim()}"
                : $"- {owner.Trim()}: {task.Trim()}");
        }

        if (lines.Length == 0) return;

        builder.AppendLine().AppendLine("Action items:").Append(lines);
    }

    /// <summary>
    /// The transcript with its markdown taken off.
    ///
    /// Line by line rather than by regex over the whole document, because the only markup the
    /// finalizer emits is per-line and structural: an ATX heading, a horizontal rule, bold around
    /// the speaker tag, and italics around a single status sentence. Anything else in the line —
    /// including asterisks somebody actually said — is left alone.
    /// </summary>
    private static string RenderTranscript(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var builder = new StringBuilder();

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            // The horizontal rule under the header. A blank line separates just as well and does
            // not read as three stray characters in a text file.
            if (line.Trim() == "---")
            {
                builder.AppendLine();
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                builder.AppendLine(line[2..].Trim());
                continue;
            }

            // `**[Nam (VI)]**: xin chào` — the bold is the speaker tag and nothing else on the
            // line is emphasised, so removing the markers is exactly the intended reading.
            if (line.Contains("**", StringComparison.Ordinal))
            {
                line = line.Replace("**", string.Empty);
            }

            // A whole line wrapped in single asterisks is one of the finalizer's status sentences
            // ("*No speech transcription recorded.*"). Only unwrapped when it is the whole line,
            // so an asterisk inside spoken text survives.
            var trimmed = line.Trim();
            if (trimmed.Length > 2 && trimmed.StartsWith('*') && trimmed.EndsWith('*'))
            {
                line = trimmed[1..^1];
            }

            builder.AppendLine(line);
        }

        return builder.ToString().Trim();
    }
}
