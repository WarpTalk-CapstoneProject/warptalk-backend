using System;

namespace WarpTalk.AssistantService.Application.DTOs;

public class AssistantMessageDto
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public string Role { get; set; } = null!;
    public string Content { get; set; } = null!;
    public string Status { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Sources this answer cited, as the stored JSON array. Null when it cited nothing.
    ///
    /// Passed through as a string rather than parsed: the shape belongs to
    /// ai_assistant_worker/citations.py, and re-modelling it here would give two places to keep
    /// in step for no gain — the client is what renders it.
    /// </summary>
    public string? SourcesJson { get; set; }
}
