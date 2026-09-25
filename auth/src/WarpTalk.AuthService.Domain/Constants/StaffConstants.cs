namespace WarpTalk.AuthService.Domain.Constants;

public static class StaffConstants
{
    public static class Statuses
    {
        public const string Active = "active";
        public const string Suspended = "suspended";
    }

    public static class RoleScopes
    {
        public const string Legacy = "legacy";
        public const string PlatformStaff = "platform_staff";
    }

    public static class Sources
    {
        /// <summary>Held the legacy platform role 'admin' when the G10 migration ran.</summary>
        public const string Migrated = "migrated";
        /// <summary>An existing account granted access directly from /admin/staff.</summary>
        public const string Invited = "invited";
        /// <summary>An address invited before it had an account, accepted on its first verified sign-in.</summary>
        public const string InvitationAccepted = "invitation_accepted";
        /// <summary>
        /// Holds the legacy 'admin' role but had no staff row — a seed script or a restore that ran
        /// after the migration. Enrolled as Super Admin so nobody is locked out by ordering.
        /// </summary>
        public const string LegacyBridge = "legacy_bridge";
    }

    /// <summary>The pre-G10 platform role. Kept in auth.user_roles only as a rollback path.</summary>
    public const string LegacyAdminRoleName = "admin";

    public const int InvitationLifetimeDays = 7;
    public const int ReasonMaxLength = 500;
    public const int RoleNameMaxLength = 50;
    public const int RoleDescriptionMaxLength = 255;
    public const int EmailMaxLength = 255;

    /// <summary>last_active_at is written at most this often per person.</summary>
    public const int LastActiveWriteIntervalMinutes = 5;
}
