using WarpTalk.AssistantService.Domain.Constants;

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

    /// <summary>
    /// Which integration path serves this plugin: <c>native</c> (a provider with its own
    /// hand-written gateway, e.g. Google Workspace) or <c>mcp</c> (a real MCP server reached over
    /// the protocol). See <c>PluginConstants.PluginKind</c>.
    /// </summary>
    public string Kind { get; set; } = PluginConstants.PluginKind.Native;

    public string? McpServerUrl { get; set; }

    public string? OAuthAuthorizationEndpoint { get; set; }

    public string? OAuthTokenEndpoint { get; set; }

    public string? OAuthRevokeEndpoint { get; set; }

    public string? OAuthRegistrationEndpoint { get; set; }

    public string? OAuthClientId { get; set; }

    /// <summary>Protected with the same purpose as user tokens; never read outside Infrastructure.</summary>
    public string? OAuthClientSecretEncrypted { get; set; }

    public DateTime? ToolsSyncedAt { get; set; }

    /// <summary>
    /// Fingerprint of the tool set last fetched from the server. A change means the server altered
    /// what it exposes, which invalidates any admin approval that had downgraded a tool to read.
    /// </summary>
    public string? ToolsManifestHash { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
