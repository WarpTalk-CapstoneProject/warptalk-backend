using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// Finds the authorization server behind an MCP server and reads its capabilities.
/// </summary>
/// <remarks>
/// Walks the chain MCP Authorization mandates: RFC 9728 protected resource metadata to locate the
/// authorization server, then RFC 8414 or OpenID Connect Discovery to describe it. Both the
/// <c>WWW-Authenticate</c> route and the well-known probe are supported, in that order, because
/// the spec requires a client to handle either.
/// <para>
/// One refusal is deliberate and must not be softened: when the authorization server does not
/// advertise <c>code_challenge_methods_supported</c>, discovery fails rather than proceeding
/// without PKCE. The spec makes verifying PKCE support a client <c>MUST</c>, and a silent
/// omission here is how a provider ends up looking like an unrelated failure much later in the
/// flow.
/// </para>
/// </remarks>
public interface IMcpAuthorizationServerDiscovery
{
    Task<Result<McpServerDiscoveryDto>> DiscoverAsync(Plugin plugin, CancellationToken ct = default);
}
