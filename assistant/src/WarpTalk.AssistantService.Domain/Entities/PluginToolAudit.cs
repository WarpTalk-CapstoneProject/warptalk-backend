namespace WarpTalk.AssistantService.Domain.Entities;

public partial class PluginToolAudit
{
    public Guid Id { get; set; }

    public Guid? WorkspaceId { get; set; }

    public Guid UserId { get; set; }

    public Guid? ConversationId { get; set; }

    public Guid? AssistantMessageId { get; set; }

    public Guid PluginId { get; set; }

    public string PluginKey { get; set; } = null!;

    public string ToolName { get; set; } = null!;

    public string? InputSummary { get; set; }

    public string ResultStatus { get; set; } = null!;

    public string? ProviderResourceRef { get; set; }

    public DateTime CreatedAt { get; set; }
}
