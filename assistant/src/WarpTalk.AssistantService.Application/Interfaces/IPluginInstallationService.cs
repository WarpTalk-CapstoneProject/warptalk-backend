using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

public interface IPluginInstallationService
{
    /// <param name="workspaceId">
    /// The workspace the user is browsing from, when there is one. WT-646: it decides which rows
    /// carry a <c>WorkspacePolicyBlockReason</c>. Null - the default, and what the personal
    /// plugins page sends - means no workspace policy applies and every row comes back unblocked,
    /// which is exactly how this behaved before the policy existed.
    /// </param>
    Task<Result<IReadOnlyList<PluginCatalogItemDto>>> ListCatalogAsync(
        Guid userId,
        Guid? workspaceId = null,
        CancellationToken ct = default);

    /// <param name="workspaceId">
    /// The workspace the install is being made from, when there is one. WT-646: null skips the
    /// workspace gate entirely, so an install outside any workspace behaves as it did before.
    /// </param>
    Task<Result<PluginCatalogItemDto>> InstallAsync(
        string pluginKey,
        Guid userId,
        Guid? workspaceId = null,
        CancellationToken ct = default);

    Task<Result> DisableAsync(string pluginKey, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Adds an MCP-backed app to the catalog, so it becomes installable by every user without a
    /// deploy or a restart.
    /// </summary>
    Task<Result<PluginCatalogItemDto>> CreateMcpPluginAsync(
        CreateMcpPluginRequest request,
        Guid userId,
        CancellationToken ct = default);
}
