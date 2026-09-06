namespace WarpTalk.AssistantService.Domain.Entities;

public partial class PluginConnection
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid PluginId { get; set; }

    public string? ProviderAccountId { get; set; }

    public string? ProviderEmail { get; set; }

    public string Status { get; set; } = null!;

    public string ScopesJson { get; set; } = "[]";

    public string? EncryptedRefreshToken { get; set; }

    public string? EncryptedAccessToken { get; set; }

    public DateTime? AccessTokenExpiresAt { get; set; }

    public DateTime? TokenRotatedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
