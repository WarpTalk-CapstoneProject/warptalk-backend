namespace WarpTalk.AssistantService.Domain.Entities;

/// <summary>A member asking their workspace Owner to add a marketplace plugin.</summary>
public class PluginRequest
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid PluginId { get; set; }

    public Guid RequestedBy { get; set; }

    /// <summary>Optional, at most <c>WorkspacePluginConstants.MaxRequestReasonLength</c> characters.</summary>
    public string? Reason { get; set; }

    /// <summary><c>pending</c>, <c>approved</c> or <c>declined</c>.</summary>
    public string Status { get; set; } = null!;

    public Guid? DecidedBy { get; set; }

    public DateTime? DecidedAt { get; set; }

    public DateTime CreatedAt { get; set; }
}
