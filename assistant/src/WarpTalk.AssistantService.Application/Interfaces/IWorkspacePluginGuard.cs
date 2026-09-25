using WarpTalk.AssistantService.Application.Helpers;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// The workspace plugin rule, applied wherever a plugin is reached for.
/// </summary>
/// <remarks>
/// WT-646 made this one boolean per workspace (AllowAnyPlugins). The marketplace (2026-09-17) makes
/// it per plugin: a plugin is usable in a workspace iff that workspace has added it, or it is a
/// private plugin the workspace owns. See <see cref="WorkspacePluginAvailability"/> for how a
/// workspace that has never curated its list is still judged by the old switch.
/// </remarks>
public interface IWorkspacePluginGuard
{
    /// <summary>
    /// What a workspace has, with no check on who is asking. For callers that have already
    /// authorised the caller themselves.
    /// </summary>
    Task<WorkspacePluginAvailability> GetAvailabilityAsync(Guid workspaceId, CancellationToken ct = default);

    /// <summary>
    /// What a workspace has, for an active member of it. Fails closed on a missing workspace and on
    /// a caller who does not belong to the one they named.
    /// </summary>
    /// <remarks>
    /// The workspace id arrives as a field of a request the caller composes. Without the membership
    /// check it would not be an identity but a choice of policy: a member of a workspace without a
    /// plugin could name one that has it and be judged by that one instead.
    /// </remarks>
    Task<Result<WorkspacePluginAvailability>> GetAvailabilityForMemberAsync(
        Guid? workspaceId,
        Guid userId,
        CancellationToken ct = default);

    /// <summary>
    /// The personal surfaces: install and connect, from the plugins page.
    /// </summary>
    /// <remarks>
    /// A null <paramref name="workspaceId"/> permits any marketplace plugin, as before: installing
    /// and connecting are personal, and the plugin is stopped where it is used - at tool execution,
    /// inside a workspace. A PRIVATE plugin is different, because merely being able to see and
    /// connect it reveals another workspace's MCP server: it is permitted only to an active member
    /// of the workspace that owns it, and never under another workspace's id.
    /// <para>
    /// A workspace id, when one IS sent, is held to the same standard as on the tool path: the
    /// caller must be an active member of it before its list is consulted, for a marketplace plugin
    /// as much as a private one. Only the absence of an id is the legacy case.
    /// </para>
    /// <para>
    /// That legacy case is a known, deliberate gap rather than an oversight. The personal plugins
    /// page and clients older than the marketplace send no workspace, and a user may belong to
    /// several workspaces with different lists, so there is no single list to judge them by. What
    /// it costs is small and bounded: a user can install and connect a marketplace plugin none of
    /// their workspaces has added, but can never run it - execution goes through
    /// <see cref="CanUsePluginInWorkspaceAsync"/>, which refuses a missing workspace outright.
    /// Closing it means making every client send a workspace first.
    /// </para>
    /// </remarks>
    Task<Result> CanUsePluginAsync(
        Guid? workspaceId,
        Guid userId,
        Plugin plugin,
        CancellationToken ct = default);

    /// <summary>
    /// The same rule for a call that is <em>inside</em> a workspace - running a tool - where a
    /// missing or borrowed workspace id is a refusal rather than an absence.
    /// </summary>
    Task<Result> CanUsePluginInWorkspaceAsync(
        Guid? workspaceId,
        Guid userId,
        Plugin plugin,
        CancellationToken ct = default);
}
