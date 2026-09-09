namespace WarpTalk.AssistantService.Domain.Entities;

public partial class PluginConfirmationToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid? WorkspaceId { get; set; }

    public Guid PluginId { get; set; }

    public string PluginKey { get; set; } = null!;

    public string ToolName { get; set; } = null!;

    public string ArgumentHash { get; set; } = null!;

    public DateTime ExpiresAt { get; set; }

    public DateTime? ConsumedAt { get; set; }

    public DateTime CreatedAt { get; set; }
}
