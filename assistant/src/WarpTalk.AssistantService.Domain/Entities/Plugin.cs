using System;
using System.Collections.Generic;

namespace WarpTalk.AssistantService.Domain.Entities;

public partial class Plugin
{
    public Guid Id { get; set; }

    public string PluginKey { get; set; } = null!;

    public string Label { get; set; } = null!;

    public string Description { get; set; } = null!;

    public string? AvatarUrl { get; set; }

    public string Provider { get; set; } = null!;

    public string RequiredScopesJson { get; set; } = null!;

    public string ToolsJson { get; set; } = null!;

    public string Kind { get; set; } = null!;

    public string? McpServerUrl { get; set; }

    public string? OAuthAuthorizationEndpoint { get; set; }

    public string? OAuthTokenEndpoint { get; set; }

    public string? OAuthRevokeEndpoint { get; set; }

    public string? OAuthRegistrationEndpoint { get; set; }

    public string? OAuthClientId { get; set; }

    public string? OAuthClientSecretEncrypted { get; set; }

    public string OAuthClientSource { get; set; } = null!;

    public bool? OAuthCimdSupported { get; set; }

    public bool? OAuthIssParameterSupported { get; set; }

    public string? OAuthTokenEndpointAuthMethod { get; set; }

    public DateTime? ToolsSyncedAt { get; set; }

    public string? ToolsManifestHash { get; set; }

    public bool IsActive { get; set; }

    /// <summary>Promotes the row into whatever "featured" strip the catalog page renders.</summary>
    public bool IsFeatured { get; set; }

    /// <summary>
    /// Explicit ordering within a listing. Defaults to 0, which means "not curated yet" and leaves
    /// the query's own secondary ordering in charge.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>Free-text grouping label for the catalog page. Null means uncategorised.</summary>
    public string? Category { get; set; }

    /// <summary>
    /// Which admin last edited the row, for the portal's audit trail. No FK - users live in
    /// AuthService's own database. Null means a migration or a seed wrote the row.
    /// </summary>
    public Guid? UpdatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
