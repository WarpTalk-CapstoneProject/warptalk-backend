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

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
