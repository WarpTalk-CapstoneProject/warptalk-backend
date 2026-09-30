namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>What a notification about a workspace needs to say about it.</summary>
/// <param name="OwnerUserId">Null when the workspace service did not report one.</param>
public record WorkspaceProfile(Guid WorkspaceId, string Name, string Slug, Guid? OwnerUserId);

/// <summary>One workspace as the platform admin's plugin controls need it.</summary>
/// <param name="Status"><c>active</c> or <c>suspended</c>; deleted workspaces are never listed.</param>
/// <param name="PlanSlug">Null when the workspace has no plan.</param>
/// <param name="AllowAnyPlugins">The legacy switch an uncurated workspace is still judged by.</param>
public record PlatformWorkspace(
    Guid WorkspaceId,
    string Name,
    string Slug,
    string Status,
    Guid? OwnerUserId,
    string? PlanSlug,
    int MemberCount,
    bool AllowAnyPlugins);

public interface IWorkspaceDirectoryClient
{
    /// <summary>
    /// The workspace's name, slug and Owner. Null when the workspace service cannot be reached or
    /// does not know the workspace; never throws.
    /// </summary>
    Task<WorkspaceProfile?> GetProfileAsync(Guid workspaceId, CancellationToken ct = default);

    /// <summary>
    /// The user ids of the workspace's ACTIVE members. Null when the workspace service cannot be
    /// reached or does not know the workspace - never an empty list standing in for "unknown".
    /// </summary>
    Task<IReadOnlyList<Guid>?> ListActiveMemberUserIdsAsync(Guid workspaceId, CancellationToken ct = default);

    /// <summary>
    /// Every workspace that is not deleted. Null when the workspace service cannot be reached -
    /// never an empty list standing in for "unknown". Never throws.
    /// </summary>
    Task<IReadOnlyList<PlatformWorkspace>?> ListPlatformWorkspacesAsync(CancellationToken ct = default);

    /// <summary>
    /// (user, workspace) for each active membership of the given users. Null when the workspace
    /// service cannot be reached. Never throws.
    /// </summary>
    Task<IReadOnlyList<(Guid UserId, Guid WorkspaceId)>?> ListActiveMembershipsAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default);
}
