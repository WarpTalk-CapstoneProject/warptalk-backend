namespace WarpTalk.AssistantService.Domain.Entities;

public partial class Plugin
{
    public Guid Id { get; set; }

    public string PluginKey { get; set; } = null!;

    public string Label { get; set; } = null!;

    public string Description { get; set; } = null!;

    public string? AvatarUrl { get; set; }

    public string Provider { get; set; } = null!;

    public string RequiredScopesJson { get; set; } = "[]";

    public string ToolsJson { get; set; } = "[]";

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
