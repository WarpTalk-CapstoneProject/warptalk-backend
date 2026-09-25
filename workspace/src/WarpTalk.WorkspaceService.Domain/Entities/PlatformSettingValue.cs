using System;

namespace WarpTalk.WorkspaceService.Domain.Entities;

/// <summary>
/// workspace.platform_setting_values: one value an operator chose for a registered platform setting
/// (WarpTalk.Shared.PlatformSettings.PlatformSettingsCatalog), at one scope. No row means "not set":
/// the reading service keeps its own deploy-time configuration.
/// </summary>
public class PlatformSettingValue
{
    public string SettingKey { get; set; } = null!;

    /// <summary>platform, plan or workspace.</summary>
    public string ScopeType { get; set; } = null!;

    /// <summary>Empty for platform; the plan slug; or the workspace id.</summary>
    public string ScopeId { get; set; } = string.Empty;

    /// <summary>The JSON value, validated against the registry before it was stored.</summary>
    public string ValueJson { get; set; } = null!;

    /// <summary>Bumped on every change; a write names the version it read (optimistic concurrency).</summary>
    public int Version { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}
