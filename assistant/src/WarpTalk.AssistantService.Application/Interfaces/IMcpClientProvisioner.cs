using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// Prepares a plugin row to start an OAuth flow: authorization server discovery, then the client
/// registration ladder, then persistence of both.
/// </summary>
/// <remarks>
/// Failure is a <see cref="Result"/> carrying a plugin error code, not an exception. The two that
/// matter are distinguished deliberately:
/// <c>client_registration_unsupported</c> is an operator action (register an app, supply a client
/// id) and <c>provider_unavailable</c> is worth retrying. Collapsing them is what turns a
/// temporary outage into a plugin that looks permanently broken.
/// </remarks>
public interface IMcpClientProvisioner
{
    Task<Result<McpClientContextDto>> ProvisionAsync(Plugin plugin, CancellationToken ct = default);
}
