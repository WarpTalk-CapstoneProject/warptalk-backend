using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Infrastructure.Mcp;

/// <summary>
/// Walks the registered <see cref="IMcpClientRegistrar"/> instances in the order they were
/// supplied, which DI registration fixes to the spec's priority order: pre-registered, then CIMD,
/// then Dynamic Client Registration.
/// </summary>
/// <remarks>
/// The order lives in <c>Program.cs</c>'s registration list, not in this class - adding, removing,
/// or reordering a rung is a DI change, never an edit here.
/// </remarks>
public class McpClientRegistrationResolver : IMcpClientRegistrationResolver
{
    private readonly IReadOnlyList<IMcpClientRegistrar> _registrars;

    public McpClientRegistrationResolver(IEnumerable<IMcpClientRegistrar> registrars)
    {
        _registrars = registrars.ToArray();
    }

    public async Task<McpClientIdentityDto> ResolveAsync(
        Plugin plugin,
        AuthorizationServerMetadataDto metadata,
        CancellationToken ct = default)
    {
        string? lastRejection = null;

        foreach (var registrar in _registrars)
        {
            if (!registrar.CanResolve(plugin, metadata)) continue;

            var identity = await registrar.ResolveAsync(plugin, metadata, ct);
            if (identity.Outcome == McpClientRegistrationOutcome.Resolved) return identity;

            if (identity.Outcome == McpClientRegistrationOutcome.ProviderUnavailable) return identity;

            // Unsupported from a registrar that claimed CanResolve: fall through to the next rung
            // rather than stopping, since a declared capability that failed on closer inspection
            // (e.g. no usable auth method) still leaves the remaining rungs a fair chance.
            lastRejection = identity.Detail;
        }

        return McpClientIdentityDto.Unsupported(
            lastRejection
                ?? "No client registration mechanism applies: the plugin has no pre-registered "
                    + "client, and the authorization server advertises neither Client ID Metadata "
                    + "Document support nor a dynamic registration endpoint.");
    }
}
