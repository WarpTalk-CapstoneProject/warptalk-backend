using System;

namespace WarpTalk.AssistantService.Domain.Entities;

/// <summary>
/// One turn of a <see cref="PlatformConversation"/>. No workspace id, no mentions, no attachments:
/// a platform turn is a question about the whole platform, answered only from the read-only admin
/// APIs, and carries nothing a workspace store would recognise.
/// </summary>
public partial class PlatformMessage
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public Guid? UserId { get; set; }

    public string Role { get; set; } = null!;

    public string Content { get; set; } = null!;

    public string? ToolResultsJson { get; set; }

    /// <summary>Cited admin pages: a JSON array of {marker, kind:"admin", title, ref}.</summary>
    public string? SourcesJson { get; set; }

    public string Status { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public virtual PlatformConversation Conversation { get; set; } = null!;
}
