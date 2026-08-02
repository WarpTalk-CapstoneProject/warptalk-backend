namespace WarpTalk.Shared;

public static class WorkspaceRoleConstants
{
    public const string Owner = "Owner";
    public const string Admin = "Admin";
    public const string SystemAdmin = "admin";
    public const string OwnerAdmin = Owner + ", " + Admin;
    public const string AdminSystem = Admin + ", " + SystemAdmin;
    public const string OwnerAdminSystem = Owner + ", " + Admin + ", " + SystemAdmin;
}
