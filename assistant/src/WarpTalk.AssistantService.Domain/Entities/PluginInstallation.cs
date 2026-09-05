namespace WarpTalk.AssistantService.Domain.Entities;

public partial class PluginInstallation
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid PluginId { get; set; }

    public string Status { get; set; } = null!;

    public string? ConfigJson { get; set; }

    public DateTime InstalledAt { get; set; }

    public DateTime? DisabledAt { get; set; }
}
