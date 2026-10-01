using System;

namespace WarpTalk.WorkspaceService.Domain.Entities;

/// <summary>workspace.admin_inbox_notes (G12): an append-only internal note on a pending-work inbox item.</summary>
public class AdminInboxNote
{
    public Guid Id { get; set; }
    public string ItemKey { get; set; } = null!;
    public string Body { get; set; } = null!;
    public Guid AuthorId { get; set; }
    public DateTime CreatedAt { get; set; }
}
