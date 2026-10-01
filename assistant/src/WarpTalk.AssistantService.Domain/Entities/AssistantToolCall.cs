using System;

namespace WarpTalk.AssistantService.Domain.Entities;

/// <summary>
/// One tool invocation within an assistant turn. Id doubles as the correlation id for
/// tool calls that hop through Redis to a Python worker (semantic search, meeting summary),
/// so the result-consumer can resolve a pending call without a separate lookup table.
/// </summary>
public partial class AssistantToolCall
{
    public Guid Id { get; set; }

    public Guid MessageId { get; set; }

    public string ToolName { get; set; } = null!;

    public string ArgumentsJson { get; set; } = null!;

    public string Status { get; set; } = null!;

    public string? ResultJson { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>The conversation's workspace. Null only on rows written before 20261001150000.</summary>
    public Guid? WorkspaceId { get; set; }

    /// <summary>The conversation's user. Null only on rows written before 20261001150000.</summary>
    public Guid? UserId { get; set; }

    /// <summary><c>builtin</c>, <c>plugin</c> or <c>web_search</c>; see <see cref="Constants.AssistantToolCallConstants.Sources"/>.</summary>
    public string Source { get; set; } = null!;

    public string? PluginKey { get; set; }

    /// <summary>See <see cref="Constants.AssistantToolCallConstants.Outcomes"/>.</summary>
    public string Outcome { get; set; } = null!;

    public string? OutcomeCode { get; set; }

    public int? DurationMs { get; set; }

    public virtual AssistantMessage Message { get; set; } = null!;
}
