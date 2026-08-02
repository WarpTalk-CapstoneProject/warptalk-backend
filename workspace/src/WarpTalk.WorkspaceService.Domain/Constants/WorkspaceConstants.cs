namespace WarpTalk.WorkspaceService.Domain.Constants;

public static class WorkspaceConstants
{
    // Workspace Settings Defaults
    public const string DefaultWorkspaceLanguage = "en";
    public const string DefaultWorkspaceTimezone = "UTC";
    public const int DefaultWorkspaceMaxActiveRooms = 5;
    public const int DefaultWorkspaceArtifactRetentionDays = 30;
    public const int MinWorkspaceMaxActiveRooms = 1;
    public const int MaxWorkspaceMaxActiveRooms = 50;
    public const int MinWorkspaceArtifactRetentionDays = 1;
    public const int MaxWorkspaceArtifactRetentionDays = 3650;

    // Invitation Defaults
    public const int DefaultInvitationExpiryDays = 7;
    public const int MinWorkspaceInvitationExpiryDays = 1;
    public const int MaxWorkspaceInvitationExpiryDays = 365;

    // Error Messages
    public static class Errors
    {
        public const string WorkspaceNameRequired = "Workspace name is required.";
        public const string UserNotFound = "User not found.";
        public const string InvalidUserEmail = "Invalid user email.";
        public const string UserAlreadyInternalElsewhere = "User is already an internal member of another Enterprise Workspace.";
        public const string DomainRegisteredElsewhere = "This email belongs to a corporate domain registered with another workspace.";
        public const string CannotVerifyPublicDomain = "Cannot verify public domains (like Gmail, Yahoo, etc.) for a workspace.";
        public const string RequiredOwnerRoleNotFound = "Required owner role not found.";
        public const string UnexpectedErrorCreatingWorkspace = "An unexpected error occurred while creating the workspace.";
        public const string UnexpectedErrorFetchingWorkspaces = "An unexpected error occurred while fetching workspaces.";
        public const string UserNotMember = "User is not a member of this workspace.";
        public const string WorkspaceNotFound = "Workspace not found.";
        public const string UnexpectedErrorFetchingWorkspace = "An unexpected error occurred while fetching the workspace.";
        public const string UnexpectedErrorSelectingWorkspace = "An unexpected error occurred while selecting the workspace.";
        public const string UnexpectedError = "An unexpected error occurred.";
        public const string UserNotActiveMember = "User is not an active member of this workspace.";
        public const string OnlyOwnerAdminCanUpdateSettings = "Only Owner or Admin can update workspace settings.";
        public const string InvalidSettingsPayload = "Invalid settings payload.";
        public const string MaxActiveRoomsOutOfRange = "Max active rooms must be between 1 and 50.";
        public const string ArtifactRetentionDaysOutOfRange = "Artifact retention days must be between 1 and 3650.";
        public const string VerifiedDomainsRequired = "Verified domains are required when internal members must use verified domains.";
        public const string InvitationExpiryDaysOutOfRange = "Invitation expiry days must be between 1 and 365.";
        public const string OnlyOwnerCanModifyExternalCollaboration = "Only the workspace owner can modify AllowExternalCollaboration setting.";
        public const string OnlyOwnerCanModifyPolicySettings = "Only the workspace owner can modify this workspace policy setting.";
        public const string OnlyOwnerCanDeleteWorkspace = "Only the workspace owner can delete the workspace.";
        
        public const string OnlyOwnerCanTransferOwnership = "Only the workspace owner can transfer ownership.";
        public const string NewOwnerMustBeActiveMember = "New owner must be an active member of the workspace.";
        public const string CannotTransferToExternal = "Cannot transfer ownership to an external member.";
        public const string RequiredRolesNotFound = "Required roles not found.";
        public const string CannotLeaveAsLastOwner = "Cannot leave the workspace as the last owner. Please transfer ownership first.";
        public const string OnlyOwnerAdminCanRemoveMembers = "Only Owner or Admin can remove members.";
        public const string TargetMemberNotFoundOrRemoved = "Target member not found or already removed.";
        public const string CannotRemoveOwner = "Cannot remove the Owner of the workspace.";
        public const string RoleMustBeAdminOrMember = "Role name must be Admin or Member.";
        public const string OnlyOwnerAdminCanChangeRoles = "Only the workspace Owner can change member roles.";
        public const string ExternalRoleImmutable = "External members can only retain the Member role.";
        public const string RoleChangeStale = "Role change preview is stale. Reload the member and preview again.";
        public const string CoolingOffNotComplete = "Promotion cooling-off has not completed.";
        public const string RoleChangePreviewExpired = "Role change preview has expired.";
        public const string InvalidRoleChangePreview = "Role change preview is invalid.";
        public const string RolePreviewSigningKeyNotConfigured = "Role preview signing key is not configured.";
        public const string InvalidIdempotencyKey = "A valid idempotency key is required.";
        public const string CannotDemoteLastOwner = "Cannot demote the last owner. Please transfer ownership first.";
        public const string CannotChangeOwnerRole = "Cannot change the Owner's role.";
        public const string CannotChangeOwnRole = "Members cannot change their own workspace role.";
        public const string AdminCannotChangeAdminRole = "Admin cannot change another Admin's role.";
        public const string AdminCannotModifyPeerAdmin = "Admin cannot modify settings of other Admins.";
        public const string AdminCannotPromoteToAdmin = "Admin cannot promote members to Admin role.";
        public const string RoleNotFound = "Role not found.";

        public const string OnlyOwnerAdminCanInvite = "Only Owner or Admin can invite members.";
        public const string AdminCannotAssignOwner = "Admin cannot assign Owner role.";
        public const string InvalidEmailFormat = "Invalid email format.";
        public const string ExternalCollaborationNotAllowed = "Workspace does not allow external collaboration.";
        public const string InvalidRoleSpecified = "Invalid role specified.";
        public const string OnlyOwnerAdminCanViewInvitations = "Only Owner or Admin can view invitations.";
        public const string OnlyOwnerAdminCanRevoke = "Only Owner or Admin can revoke invitations.";
        public const string InvitationNotFound = "Invitation not found.";
        public const string OnlyPendingCanBeRevoked = "Only pending invitations can be revoked.";
        public const string InvalidOrExpiredToken = "Invalid or expired invitation token.";
        public const string TokenRequired = "Token is required.";
        public const string InvitationExpired = "Invitation has expired.";
        public const string EmailMismatch = "The email used for registration does not match the invitation email.";
        public const string AlreadyMember = "You are already a member of this workspace.";
        public const string InvitationNoLongerValidFormat = "Invitation is no longer valid. Status: {0}";
        public const string InvalidMembershipType = "Invalid membership type specified. Must be Internal or External.";
        public const string CannotInviteInternalWithoutVerifiedDomain = "Cannot invite as an Internal member because the email domain is not verified for this workspace.";
        public const string ExternalMemberMustHaveMemberRole = "External members can only be assigned the Member role.";

        // Join Request Errors
        public const string TranslationRoomNotFound = "Translation room not found.";
        public const string RoomCodeOrWorkspaceSlugRequired = "Room code or workspace slug is required.";
        public const string MemberRoleNotFound = "Default member role not found.";
        public const string OnlyOwnerAdminCanApprove = "Only Owner or Admin can approve join requests.";
        public const string OnlyRequestedCanBeApproved = "Only join requests in REQUESTED status can be approved.";
        public const string OnlyOwnerAdminCanReject = "Only Owner or Admin can reject join requests.";
        public const string OnlyRequestedCanBeRejected = "Only join requests in REQUESTED status can be rejected.";

        // Verified domain errors
        public const string VerifiedDomainNotFound = "Verified domain entry not found.";
        public const string DomainAlreadyAddedToWorkspace = "This domain has already been added to this workspace.";
        public const string CannotRevokeLastDomain = "Cannot revoke the last verified domain while the workspace requires domain verification for Internal members. Add another domain first or disable the requirement.";
        public const string CannotRevokeDomainWithActiveMembers = "Cannot revoke domain because active internal members are still using this domain. Please update or remove these members first.";
        public const string OnlyOwnerCanManageDomains = "Only the workspace Owner can add or revoke verified domains.";

        // Document specific errors
        public const string DocumentNotFound = "Document not found.";
        public const string AccessDeniedNotMember = "Access denied. User is not a active member of this workspace.";
        public const string AccessDeniedPendingIngestion = "Access denied. Document ingestion is pending.";
        public const string AccessDeniedByPolicy = "Access denied by policy (DENY).";
        public const string AccessDeniedSensitive = "Access denied. Sensitive document.";
        public const string AccessDeniedDefault = "Access denied. Default action blocks access.";
    }

    // Configuration Keys
    public const string DefaultExternalGracePeriodHoursKey = "GracePeriodSettings:DefaultExternalGracePeriodHours";
}
