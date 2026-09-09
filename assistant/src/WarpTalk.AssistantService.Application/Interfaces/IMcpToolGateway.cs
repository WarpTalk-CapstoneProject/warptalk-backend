using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Application.Interfaces;

public interface IMcpToolGateway
{
    /// <summary>
    /// Fetches the tool set a plugin's server currently exposes.
    /// </summary>
    /// <remarks>
    /// Called at connect time, not at list time. For a <c>native</c> row the tools are authored by
    /// us and this simply echoes what the catalog already holds; for an <c>mcp</c> row it is a live
    /// <c>tools/list</c> whose result is cached into <c>tools_json</c>, which is what makes a remote
    /// server's tools appear without a deploy.
    /// <para>
    /// Deliberately not gated by workspace policy: this runs inside the OAuth callback, after the
    /// user has consented at the provider, and a callback carries no workspace context to judge
    /// against. WT-646 kept that placement and added the gates one step earlier, where a workspace
    /// IS known - the catalog and install paths in <c>PluginInstallationService</c>,
    /// <c>PluginConnectionService.GetConnectUrlAsync</c>, and listing and executing in
    /// <c>McpToolOrchestrator</c>. All of them go through <c>IWorkspacePluginGuard</c>.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<McpToolDescriptorDto>> ListToolsAsync(
        PluginDefinitionDto plugin,
        PluginConnection connection,
        CancellationToken ct = default);

    Task<McpToolExecutionResult> ExecuteAsync(
        PluginDefinitionDto plugin,
        McpToolDescriptorDto tool,
        PluginConnection connection,
        McpToolExecutionRequest request,
        CancellationToken ct = default);
}
