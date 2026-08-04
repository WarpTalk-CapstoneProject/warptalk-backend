namespace WarpTalk.WorkspaceService.Domain.Constants;

/// <summary>
/// Lifecycle actions a system admin can take on a workspace. Values match the
/// workspace_admin_actions.action CHECK constraint.
/// </summary>
public static class WorkspaceAdminActionTypes
{
    public const string Suspend = "suspend";
    public const string Reactivate = "reactivate";
}

/// <summary>
/// Derived lifecycle status of a workspace, as reported by the admin directory.
/// Active and suspended are both <c>deleted_at IS NULL</c>; they differ by is_active.
/// </summary>
public static class WorkspaceLifecycleStatus
{
    public const string All = "all";
    public const string Active = "active";
    public const string Suspended = "suspended";
    public const string Deleted = "deleted";
}

/// <summary>Accepted sort keys for the admin workspace directory.</summary>
public static class WorkspaceDirectorySort
{
    public const string CreatedDesc = "created_desc";
    public const string CreatedAsc = "created_asc";
    public const string NameAsc = "name_asc";
    public const string NameDesc = "name_desc";
    public const string MembersDesc = "members_desc";
    public const string MembersAsc = "members_asc";
    public const string UpdatedDesc = "updated_desc";
}

public static class WorkspaceAdminErrors
{
    public const string ReasonRequired = "A reason is required for workspace lifecycle actions.";
    public const string ReasonTooLong = "Reason must be 500 characters or fewer.";
    public const string AlreadySuspended = "Workspace is already suspended.";
    public const string AlreadyActive = "Workspace is already active.";
    public const string DeletedWorkspaceIsImmutable =
        "Workspace has been deleted and its lifecycle can no longer be changed.";
    public const string UnknownStatusFilter = "Unknown status filter.";
    public const string UnknownSort = "Unknown sort key.";
    public const string InvalidMemberCountRange =
        "minMembers must be less than or equal to maxMembers.";
}
