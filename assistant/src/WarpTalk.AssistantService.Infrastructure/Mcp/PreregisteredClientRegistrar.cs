using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Infrastructure.Mcp;

/// <summary>
/// Rung 1: the row already carries a client id, so nothing needs to be negotiated.
/// </summary>
/// <remarks>
/// This rung also covers the case a client that only implements dynamic registration gets wrong:
/// a row whose credentials came from an earlier <c>/register</c> call still has a usable client id,
/// and re-registering on every connect is both wasteful and how servers end up pruning the
/// registration out from under you. So the test is "is there a client id", not "did an operator
/// type it in", and the recorded source is preserved rather than relabelled.
/// </remarks>
public class PreregisteredClientRegistrar : IMcpClientRegistrar
{
    public string Source => PluginConstants.OAuthClientSource.Preregistered;

    public bool CanResolve(Plugin plugin, AuthorizationServerMetadataDto metadata) =>
        !string.IsNullOrWhiteSpace(plugin.OAuthClientId);

    public Task<McpClientIdentityDto> ResolveAsync(
        Plugin plugin,
        AuthorizationServerMetadataDto metadata,
        CancellationToken ct = default)
    {
        if (!CanResolve(plugin, metadata))
        {
            return Task.FromResult(McpClientIdentityDto.Unsupported(
                "No client id is configured for this plugin."));
        }

        // A row that already registered dynamically keeps saying so; only a genuinely new
        // operator-supplied client is labelled 'preregistered'.
        var source = plugin.OAuthClientSource == PluginConstants.OAuthClientSource.Dcr
            ? PluginConstants.OAuthClientSource.Dcr
            : PluginConstants.OAuthClientSource.Preregistered;

        return Task.FromResult(McpClientIdentityDto.Resolved(
            plugin.OAuthClientId!,
            source,
            ChooseAuthMethod(plugin, metadata),
            plugin.OAuthClientSecretEncrypted));
    }

    /// <summary>
    /// A confidential client authenticates with its secret where the server accepts one; without a
    /// secret it is a public client and says so. Preference order follows OAuth 2.1, which favours
    /// <c>client_secret_post</c> over the Basic form.
    /// </summary>
    private static string ChooseAuthMethod(Plugin plugin, AuthorizationServerMetadataDto metadata)
    {
        if (string.IsNullOrWhiteSpace(plugin.OAuthClientSecretEncrypted))
            return PluginConstants.TokenEndpointAuthMethod.None;

        var accepted = metadata.TokenEndpointAuthMethodsSupported;
        if (accepted.Count == 0)
            return PluginConstants.TokenEndpointAuthMethod.ClientSecretPost;

        foreach (var preferred in new[]
                 {
                     PluginConstants.TokenEndpointAuthMethod.ClientSecretPost,
                     PluginConstants.TokenEndpointAuthMethod.ClientSecretBasic,
                 })
        {
            if (accepted.Contains(preferred, StringComparer.Ordinal)) return preferred;
        }

        return PluginConstants.TokenEndpointAuthMethod.None;
    }
}
