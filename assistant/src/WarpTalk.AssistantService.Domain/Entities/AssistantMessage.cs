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

    public string Status { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public virtual AssistantConversation Conversation { get; set; } = null!;

    public virtual ICollection<AssistantToolCall> ToolCalls { get; set; } = new List<AssistantToolCall>();
}
