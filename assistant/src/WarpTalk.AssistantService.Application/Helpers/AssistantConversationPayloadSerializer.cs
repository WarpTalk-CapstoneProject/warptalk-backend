using System.Text.Json;
using WarpTalk.AssistantService.Application.DTOs;

namespace WarpTalk.AssistantService.Application.Helpers;

internal static class AssistantConversationPayloadSerializer
{
    internal static string? SerializeAttachments(List<AssistantAttachmentDto>? attachments)
    {
        if (attachments == null || attachments.Count == 0) return null;

        var accepted = attachments
            .Where(attachment => !string.IsNullOrWhiteSpace(attachment.DataUrl))
            .Where(attachment => attachment.DataUrl.StartsWith("data:", StringComparison.Ordinal))
            .Where(attachment => attachment.DataUrl.Length <= 7_000_000)
            .Where(attachment => IsSupportedAttachment(attachment.DataUrl))
            .Take(4)
            .Select(attachment => new
            {
                dataUrl = attachment.DataUrl,
                name = attachment.Name ?? "",
                mimeType = attachment.MimeType ?? "",
            })
            .ToList();

        return accepted.Count == 0 ? null : JsonSerializer.Serialize(accepted);
    }

    internal static string? SerializePageContext(AssistantPageContextDto? pageContext, Guid conversationWorkspaceId)
    {
        if (pageContext == null || string.IsNullOrWhiteSpace(pageContext.PageType))
            return null;

        if (!string.IsNullOrEmpty(pageContext.WorkspaceId)
            && Guid.TryParse(pageContext.WorkspaceId, out var contextWorkspaceId)
            && contextWorkspaceId != conversationWorkspaceId)
        {
            return null;
        }

        return JsonSerializer.Serialize(new
        {
            pageType = pageContext.PageType,
            entityId = pageContext.EntityId,
            workspaceId = conversationWorkspaceId.ToString(),
            snapshot = pageContext.Snapshot,
        });
    }

    internal static string? SerializeMentions(List<AssistantMentionDto>? mentions, Guid conversationWorkspaceId)
    {
        if (mentions == null || mentions.Count == 0)
            return null;

        var sanitized = mentions
            .Where(m => !string.IsNullOrWhiteSpace(m.EntityType) && !string.IsNullOrWhiteSpace(m.EntityId))
            .Select(m => new
            {
                entityType = m.EntityType,
                entityId = m.EntityId,
                label = m.Label,
                workspaceId = conversationWorkspaceId.ToString(),
            })
            .ToList();

        return sanitized.Count == 0 ? null : JsonSerializer.Serialize(sanitized);
    }

    /// <summary>Most plugin keys one turn will carry. The catalog is far smaller; this only bounds a hostile client.</summary>
    private const int MaxDisabledPluginKeys = 64;

    /// <summary>
    /// WT-687: the plugins switched off for this turn, as a JSON array of distinct keys, or null
    /// when none are.
    /// </summary>
    internal static string? SerializeDisabledPluginKeys(List<string>? pluginKeys)
    {
        if (pluginKeys == null || pluginKeys.Count == 0)
            return null;

        var keys = pluginKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(MaxDisabledPluginKeys)
            .ToList();

        return keys.Count == 0 ? null : JsonSerializer.Serialize(keys);
    }

    private static bool IsSupportedAttachment(string dataUrl)
    {
        var semicolon = dataUrl.IndexOf(';', StringComparison.Ordinal);
        if (semicolon <= 5) return false;

        var mime = dataUrl[5..semicolon];
        return mime.StartsWith("image/", StringComparison.Ordinal)
            || SupportedDocumentMimeTypes.Contains(mime);
    }

    private static readonly HashSet<string> SupportedDocumentMimeTypes = new(StringComparer.Ordinal)
    {
        "application/pdf",
        "text/plain",
        "text/markdown",
        "text/csv",
        "application/json",
    };
}
