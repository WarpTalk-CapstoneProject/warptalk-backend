using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Application.Mappers;

/// <summary>
/// Writes what discovery and the registration ladder learned back onto the catalog row.
/// </summary>
/// <remarks>
/// The registrars deliberately do not touch the entity - a rung that fails halfway must not leave a
/// half-committed identity behind - so persistence is collected here, in one place, where the order
/// of writes and the "only on success" rule are visible together.
/// </remarks>
public static class McpClientRegistrationMapper
{
    /// <summary>
    /// Caches the authorization server's endpoints and capabilities on the row so the next connect
    /// does not re-walk the well-known documents.
    /// </summary>
    public static void ApplyDiscovery(Plugin plugin, McpServerDiscoveryDto discovery)
    {
        var metadata = discovery.AuthorizationServer;

        plugin.OAuthAuthorizationEndpoint = metadata.AuthorizationEndpoint;
        plugin.OAuthTokenEndpoint = metadata.TokenEndpoint;
        plugin.OAuthRevokeEndpoint = metadata.RevocationEndpoint;
        plugin.OAuthRegistrationEndpoint = metadata.RegistrationEndpoint;
        plugin.OAuthCimdSupported = metadata.ClientIdMetadataDocumentSupported;
        plugin.OAuthIssParameterSupported = metadata.IssParameterSupported;
    }

    /// <summary>
    /// Records the resolved client identity. Only called for
    /// <see cref="McpClientRegistrationOutcome.Resolved"/>: an unsupported or unavailable outcome
    /// must leave the row exactly as it was, so a transient failure cannot be mistaken later for a
    /// settled answer.
    /// </summary>
    public static void ApplyClientIdentity(Plugin plugin, McpClientIdentityDto identity)
    {
        if (identity.Outcome != McpClientRegistrationOutcome.Resolved) return;

        plugin.OAuthClientSource = identity.Source!;
        plugin.OAuthTokenEndpointAuthMethod = identity.TokenEndpointAuthMethod;

        // A CIMD client's id is our own metadata URL, which is configuration rather than
        // per-plugin state - persisting it would silently pin the row to today's URL and defeat
        // the versioning that lets the document move to /v2.json. Only credentials the row
        // genuinely owns are stored.
        if (identity.Source == Domain.Constants.PluginConstants.OAuthClientSource.Cimd)
        {
            plugin.OAuthClientId = null;
            plugin.OAuthClientSecretEncrypted = null;
            return;
        }

        plugin.OAuthClientId = identity.ClientId;
        plugin.OAuthClientSecretEncrypted = identity.EncryptedClientSecret;
    }
}
