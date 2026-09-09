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
    /// It is <c>Plugin.Provider</c>, copied at consent time rather than joined to, because the
    /// connection outlives any single catalog row. Every write path that creates a connection sets
    /// it; nothing here should rely on a default.
    /// <para>
    /// The column is <c>NOT NULL</c> and, for now, carries a server-side <c>DEFAULT 'unknown'</c>.
    /// That default is a rolling-deploy shim, not part of the model: deploys are migration-first,
    /// so between 20260907101000 landing and the last pre-WT-646 pod retiring there are pods
    /// inserting connections without this column, and a bare <c>NOT NULL</c> would answer them with
    /// a 23502 on the OAuth callback. A row written that way lands under <c>'unknown'</c>, which no
    /// OAuth client answers to and which collides with nothing, so it is inert rather than wrong
    /// and the user's next consent writes a correct row. The default is dropped by a contract
    /// migration in the release after that one.
    /// </para>
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
