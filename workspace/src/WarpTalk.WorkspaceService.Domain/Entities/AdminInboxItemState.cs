using System;

namespace WarpTalk.WorkspaceService.Domain.Entities;

/// <summary>
/// workspace.admin_inbox_item_states (G12): what staff added on top of one pending-work inbox item —
/// assignment, snooze, a manual "done". The item itself lives in its source service; this row is keyed
/// on the item's stable key and outlives it harmlessly.
/// </summary>
public class AdminInboxItemState
{
    public string ItemKey { get; set; } = null!;
    public string ItemType { get; set; } = null!;
    public Guid? AssigneeId { get; set; }
    public Guid? AssignedBy { get; set; }
    public DateTime? AssignedAt { get; set; }
    public DateTime? SnoozedUntil { get; set; }
    public Guid? SnoozedBy { get; set; }
    public DateTime? DoneAt { get; set; }
    public Guid? DoneBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}
