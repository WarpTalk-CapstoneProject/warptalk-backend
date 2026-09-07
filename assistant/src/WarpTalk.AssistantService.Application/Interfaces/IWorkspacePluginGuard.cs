using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// The one workspace-level plugin rule, applied wherever a plugin is reached for. WT-646.
/// </summary>
/// <remarks>
/// A workspace owner configures exactly one thing: whether members may use plugins in that
/// workspace at all. This is where that boolean is read and turned into a refusal, so the
/// no-workspace case and the wording of the refusal are written once rather than at each of the
/// four call sites - the catalog, an install, a connect, and the tool list and execution.
/// </remarks>
public interface IWorkspacePluginGuard
{
    /// <summary>
    /// Whether plugins may be used in this workspace.
    /// </summary>
    /// <remarks>
    /// A null <paramref name="workspaceId"/> succeeds without any round trip. That is every
    /// personal call - the plugins settings page, an install, a connect - made outside a workspace
    /// context, and it must permit everything: workspace policy governs what a workspace's members
    /// may do IN that workspace, and a request naming no workspace has none to apply.
    /// </remarks>
    Task<Result> CanUsePluginsAsync(Guid? workspaceId, CancellationToken ct = default);
}
