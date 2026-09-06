using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// Establishes the OAuth client identity a plugin presents to its authorization server.
/// </summary>
/// <remarks>
/// MCP Authorization 2026-07-28 does not offer a choice of registration mechanism; it fixes a
/// priority order and expects a client to walk it: pre-registered client information first, then a
/// Client ID Metadata Document when the server advertises support, then Dynamic Client
/// Registration - which the same document marks deprecated and retained only for servers without
/// CIMD. This interface is that ladder, so that adding or removing a rung is adding or removing an
/// implementation rather than editing a branch inside the OAuth client.
/// <para>
/// Exhausting the ladder is a <em>return value</em>, not an exception. That distinction is the
/// whole reason this type exists separately from <see cref="IPluginOAuthClient"/>: a server with
/// no dynamic registration and no CIMD is an ordinary, recoverable situation that an operator
/// fixes by registering an app, and the user needs to be told so. Throwing there is what makes the
/// reference MCP clients unusable against Cognito, Box and Slack - the failure surfaces as a raw
/// "does not support dynamic client registration" string at the status call, before anyone is
/// offered a way forward.
/// </para>
/// <para>
/// Implementations must not write to the plugin row. Persisting the resolved rung is the caller's
/// job, so that a failed or transient attempt cannot leave a half-committed identity behind.
/// </para>
/// </remarks>
public interface IMcpClientRegistrar
{
    /// <summary>
    /// The rung this registrar serves, matching a <c>PluginConstants.OAuthClientSource</c> value.
    /// Registrars are consulted in the spec's priority order, and each one declines by returning
    /// <see cref="McpClientRegistrationOutcome.Unsupported"/> rather than throwing.
    /// </summary>
    string Source { get; }

    /// <summary>
    /// Whether this registrar can serve <paramref name="plugin"/> against a server described by
    /// <paramref name="metadata"/>. Cheap and side-effect free: it reads the row and the server's
    /// advertised capabilities, and performs no network calls.
    /// </summary>
    bool CanResolve(Plugin plugin, AuthorizationServerMetadataDto metadata);

    /// <summary>
    /// Produces the client identity, performing any network work the rung requires - dynamic
    /// registration posts to the server, the other two rungs do not.
    /// </summary>
    Task<McpClientIdentityDto> ResolveAsync(
        Plugin plugin,
        AuthorizationServerMetadataDto metadata,
        CancellationToken ct = default);
}

/// <summary>
/// Walks the registrars in the spec's priority order and returns the first identity produced.
/// </summary>
public interface IMcpClientRegistrationResolver
{
    /// <summary>
    /// Returns <see cref="McpClientRegistrationOutcome.Unsupported"/> when no rung applies, and
    /// <see cref="McpClientRegistrationOutcome.ProviderUnavailable"/> when a rung that should have
    /// applied failed for reasons that say nothing about the server's capabilities.
    /// </summary>
    Task<McpClientIdentityDto> ResolveAsync(
        Plugin plugin,
        AuthorizationServerMetadataDto metadata,
        CancellationToken ct = default);
}
