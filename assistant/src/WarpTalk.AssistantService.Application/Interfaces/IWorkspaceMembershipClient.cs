using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// One user's standing in one workspace, as the workspace service reports it. WT-646.
/// </summary>
/// <remarks>
/// The assistant service owns plugin installations but not workspace roles, so
/// <c>allow_member_plugin_install</c> - which distinguishes an Owner or Admin from a plain Member -
/// cannot be applied without asking the workspace service who the caller is.
/// </remarks>
/// <param name="IsMember">Whether the user belongs to the workspace at all.</param>
/// <param name="RoleName">
/// The workspace role name. Compared case-insensitively everywhere in this codebase, because it is
/// stored as a display-cased string ("Owner", "Admin") rather than an enum.
/// </param>
/// <param name="IsActive">
/// The MEMBER's status, not the workspace's - a suspended workspace still reports active members.
/// </param>
public record WorkspaceMembership(bool IsMember, string RoleName, bool IsActive)
{
    /// <summary>
    /// What to assume when the workspace service cannot be reached or does not know the pair.
    /// Not a member, so every check that widens a permission stays closed.
    /// </summary>
    public static WorkspaceMembership None { get; } = new(IsMember: false, RoleName: string.Empty, IsActive: false);

    /// <summary>
    /// Whether this membership carries workspace administrative authority. An inactive member has
    /// none, whatever their role says.
    /// </summary>
    public bool IsOwnerOrAdmin =>
        IsMember
        && IsActive
        && (string.Equals(RoleName, WorkspaceRoleConstants.Owner, StringComparison.OrdinalIgnoreCase)
            || string.Equals(RoleName, WorkspaceRoleConstants.Admin, StringComparison.OrdinalIgnoreCase));
}

public interface IWorkspaceMembershipClient
{
    /// <summary>
    /// Resolves the caller's membership. Never throws: an unreachable workspace service yields
    /// <see cref="WorkspaceMembership.None"/>, so a transport failure denies rather than grants.
    /// </summary>
    Task<WorkspaceMembership> GetMembershipAsync(Guid workspaceId, Guid userId, CancellationToken ct = default);
}
