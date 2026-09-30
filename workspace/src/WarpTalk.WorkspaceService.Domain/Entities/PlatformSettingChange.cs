using System;

namespace WarpTalk.WorkspaceService.Domain.Entities;

/// <summary>
/// workspace.platform_setting_changes: append-only history of every platform setting change —
/// who, when, why, and the value before and after. A revert is a new change, never an edit.
/// </summary>
public class PlatformSettingChange
{
    public Guid Id { get; set; }
    public string SettingKey { get; set; } = null!;
    public string ScopeType { get; set; } = null!;
    public string ScopeId { get; set; } = string.Empty;

    /// <summary>set, reset, revert or import.</summary>
    public string Action { get; set; } = null!;

    /// <summary>JSON before the change; null when nothing was set.</summary>
    public string? OldValueJson { get; set; }

    /// <summary>JSON after the change; null when the change removed the value.</summary>
    public string? NewValueJson { get; set; }

    /// <summary>The value row's version after this change (0 for a reset).</summary>
    public int Version { get; set; }

    public string? Reason { get; set; }
    public Guid ChangedBy { get; set; }
    public string? ChangedByEmail { get; set; }
    public string? ChangedByName { get; set; }
    public DateTime ChangedAt { get; set; }
    public string? CorrelationId { get; set; }

    /// <summary>For a revert: the change whose "before" value was restored.</summary>
    public Guid? RevertOf { get; set; }
}
