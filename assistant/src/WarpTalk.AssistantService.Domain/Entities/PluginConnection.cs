namespace WarpTalk.AssistantService.Domain.Entities;

public partial class PluginConnection
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>
    /// Which catalog row first sent this user to consent. Provenance only.
    /// </summary>
    /// <remarks>
    /// It stopped being the identity of the connection in 20260907101000. One Google grant now
    /// serves google_drive, google_calendar and google_meet, so looking a connection up by this
    /// column finds it for at most one of the three and reports the other two as not connected.
    /// Look up by <see cref="Provider"/>.
    /// </remarks>
    public Guid PluginId { get; set; }

    /// <summary>
    /// The provider this grant is with - the identity of the connection, unique per user.
    /// </summary>
    /// <remarks>
    /// <c>NOT NULL</c> with no database default, so every write path that creates a connection has
    /// to set it or the insert throws. It is <c>Plugin.Provider</c>, copied at consent time rather
    /// than joined to, because the connection outlives any single catalog row.
    /// </remarks>
    public string Provider { get; set; } = null!;

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
