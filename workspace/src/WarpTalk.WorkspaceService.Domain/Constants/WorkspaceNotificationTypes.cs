namespace WarpTalk.WorkspaceService.Domain.Constants;

/// <summary>
/// The notification types this service emits.
///
/// A NEW TYPE IS TWO EDITS IN TWO SERVICES, AND THE PRODUCER CANNOT TELL YOU WHEN YOU HAVE ONLY
/// MADE ONE. Every string here must also appear in the notification service's
/// <c>NotificationValidator.Schemas</c> table. A type that is not in that table and carries any
/// metadata is rejected outright with <c>UNSUPPORTED_NOTIFICATION_TYPE</c>, and
/// <c>SendNotification</c> answers <c>Success=false</c> — which no producer in this codebase
/// reads. The notification is built, sent, logged as sent, and thrown away.
///
/// This has now happened four times: MEETING_STARTED and MEETING_SUMMARY_READY (Aug 2026),
/// MEETING_INVITED (538 invitations, zero rows — WT-415), and WORKSPACE_ROLE_CHANGED, which
/// WT-431 shipped as a producer with no schema behind it and which has been silently discarded
/// ever since. Registering these four together is why the constants live in one named place
/// instead of as literals at the call sites.
/// </summary>
public static class WorkspaceNotificationTypes
{
    /// <summary>To every Owner and Admin: a member has asked to leave.</summary>
    public const string LeaveRequested = "WORKSPACE_LEAVE_REQUESTED";

    /// <summary>To the member: the request was approved and their membership has ended.</summary>
    public const string LeaveApproved = "WORKSPACE_LEAVE_APPROVED";

    /// <summary>To the member: the request was declined and they are still a member.</summary>
    public const string LeaveRejected = "WORKSPACE_LEAVE_REJECTED";

    /// <summary>
    /// To the member: an Owner or Admin removed them from the workspace.
    ///
    /// Deliberately NOT the same type as <see cref="LeaveApproved"/>. Both end a membership, but
    /// one is the person's own request being granted and the other is a decision made about them
    /// — and a reader who is told "you have left" when they were removed has been told something
    /// untrue about their own actions.
    /// </summary>
    public const string MemberRemoved = "WORKSPACE_MEMBER_REMOVED";

    /// <summary>
    /// To the member: their role changed. Emitted by WorkspaceMemberService since WT-431 and
    /// unregistered on the validator until WT-521, so no such notification has ever been stored.
    /// </summary>
    public const string RoleChanged = "WORKSPACE_ROLE_CHANGED";
}
