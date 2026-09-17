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
