using System;
using System.Collections.Generic;

namespace WarpTalk.AssistantService.Domain.Entities;

public partial class AssistantMessage
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid? UserId { get; set; }

    public string Role { get; set; } = null!;

    public string Content { get; set; } = null!;

    public string? ToolCallsJson { get; set; }

    public string? ToolResultsJson { get; set; }

    /// <summary>
    /// Sources this answer actually cited: a JSON array of {marker, kind, title, ref?}.
    ///
    /// The intersection of what tools retrieved and what the answer pointed at — never the list
    /// of tools that ran. NULL or an empty array both mean "cited nothing", which is the normal
    /// case for a reply drawn from the conversation rather than a tool result.
    /// </summary>
    public string? SourcesJson { get; set; }

    /// <summary>
    /// The explicit @mentions a USER message was sent with: a JSON array of
    /// {entityType, entityId, label, workspaceId} — byte for byte the string the same turn handed
    /// the worker.
    ///
    /// Stored so the thread can show, after a reload, what the user pointed WarpBot at. Before it
    /// existed the mentions went to the worker and nowhere else: a reopened conversation read
    /// "set up a meeting for me" with no sign a plugin had been named, which is the one fact that
    /// explains why the answer reached for it. NULL means the message named nothing — or was sent
    /// before this column existed, which cannot be told apart and is not backfilled.
    /// </summary>
    public string? MentionsJson { get; set; }

    public string Status { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public virtual AssistantConversation Conversation { get; set; } = null!;

    public virtual ICollection<AssistantToolCall> ToolCalls { get; set; } = new List<AssistantToolCall>();
}
