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

    /// <summary>
    /// The same rule for a call that is <em>inside</em> a workspace, where a missing or borrowed
    /// workspace id is a refusal rather than an absence.
    /// </summary>
    /// <remarks>
    /// Two things separate this from <see cref="CanUsePluginsAsync"/>, and both exist because the
    /// workspace id arrives as an ordinary field of a request the caller composes.
    /// <para>
    /// It fails closed on null. The permissive null branch above is correct for the personal
    /// surfaces - the plugins settings page genuinely has no workspace - but on the tool paths it
    /// meant the workspace's one plugin control could be defeated by omitting a field. A tool call
    /// always happens inside a conversation, and ChatRequestMessage.workspace_id is non-optional,
    /// so nothing legitimate arrives here without one.
    /// </para>
    /// <para>
    /// And it checks that the caller actually belongs to the workspace they named. Otherwise the id
    /// is not an identity but a choice of policy: a member of a locked-down workspace could name
    /// their own permissive one and be judged by it, and could stamp any workspace's id onto the
    /// audit rows its Owner reads.
    /// </para>
    /// </remarks>
    Task<Result> CanUsePluginsInWorkspaceAsync(
        Guid? workspaceId,
        Guid userId,
        CancellationToken ct = default);
}
