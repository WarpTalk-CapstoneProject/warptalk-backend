namespace WarpTalk.WorkspaceService.Domain.Entities;

/// <summary>The values of platform_setting_values.scope_type.</summary>
public static class PlatformSettingScopeTypes
{
    public const string Platform = "platform";
    public const string Plan = "plan";
    public const string Workspace = "workspace";
}

/// <summary>The values of platform_setting_changes.action.</summary>
public static class PlatformSettingChangeActions
{
    public const string Set = "set";
    public const string Reset = "reset";
    public const string Revert = "revert";
    public const string Import = "import";
}
