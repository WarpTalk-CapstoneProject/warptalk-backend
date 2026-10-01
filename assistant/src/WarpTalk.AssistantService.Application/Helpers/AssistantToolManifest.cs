using System.Globalization;
using System.Text.Json;
using WarpTalk.AssistantService.Application.DTOs;

namespace WarpTalk.AssistantService.Application.Helpers;

/// <summary>
/// The AI worker's built-in tool manifest (<see cref="AssistantToolConstants.ManifestRedisKey"/>),
/// as written by ai_assistant_worker from its own tool registry.
/// </summary>
/// <param name="GeneratedAt">When the worker wrote it, in UTC; null when it did not say.</param>
/// <param name="WebSearchAvailable">
/// The worker's environment ceiling for web search: the provider key is present and the env flag is
/// not off. Settings can only narrow it.
/// </param>
public sealed record AssistantToolManifest(
    DateTime? GeneratedAt,
    bool WebSearchAvailable,
    IReadOnlyList<AssistantBuiltInToolDto> Tools)
{
    /// <summary>
    /// Reads the stored JSON, or null when there is none or it is not a manifest: not JSON, not an
    /// object, or without a <c>tools</c> array. A single malformed tool entry (no name) is skipped
    /// rather than costing the whole list.
    /// </summary>
    public static AssistantToolManifest? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("tools", out var toolsElement) || toolsElement.ValueKind != JsonValueKind.Array)
                return null;

            var tools = new List<AssistantBuiltInToolDto>();
            foreach (var item in toolsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var name = ReadString(item, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                tools.Add(new AssistantBuiltInToolDto(
                    name,
                    ReadString(item, "category") ?? "other",
                    ReadString(item, "effect") ?? "read",
                    ReadString(item, "audience") ?? AssistantToolConstants.Audience.Member,
                    ReadString(item, "description") ?? string.Empty));
            }

            var webSearchAvailable =
                root.TryGetProperty("webSearch", out var webSearch)
                && webSearch.ValueKind == JsonValueKind.Object
                && webSearch.TryGetProperty("available", out var available)
                && available.ValueKind == JsonValueKind.True;

            return new AssistantToolManifest(ReadTimestamp(root), webSearchAvailable, tools);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTime? ReadTimestamp(JsonElement root) =>
        ReadString(root, "generatedAt") is { } raw
        && DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.UtcDateTime
            : null;
}
