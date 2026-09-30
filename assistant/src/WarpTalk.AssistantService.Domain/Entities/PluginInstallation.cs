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

    /// <summary>
    /// When the user connected THIS plugin, or null when they have not.
    /// </summary>
    /// <remarks>
    /// The OAuth grant is keyed by provider, so one Google grant can cover Drive, Calendar and Meet
    /// at once - Calendar and Meet even ask for the same scope. Reading "connected" off the grant
    /// alone switched every sibling on the moment the user connected one of them. This is the
    /// per-plugin half of the answer: the grant says what the provider will honour, this says which
    /// plugins the user actually chose to connect.
    /// </remarks>
    public DateTime? ConnectedAt { get; set; }
}
