namespace WarpTalk.TranslationRoomService.Domain.Constants;

public static class MeetingMinutesConstants
{
    public const string StatusDraft = "DRAFT";
    public const string StatusInReview = "IN_REVIEW";
    public const string StatusApproved = "APPROVED";

    public const string ErrorRoomNotFound = "Meeting not found.";
    public const string ErrorMinutesNotFound = "This meeting has no minutes yet.";
    public const string ErrorUnauthorizedRead = "You do not have access to this meeting.";

    /// <summary>
    /// Said to somebody who may read the MEETING but not its unpublished minutes.
    ///
    /// Deliberately not "not found": they can see the meeting, so claiming it has no minutes would
    /// be a lie they can catch. It names who is holding it and what changes, which is the same
    /// shape ArtifactAccessHelper.DescribeArtifactDenial uses for the record.
    /// </summary>
    public const string ErrorMinutesNotPublished =
        "These minutes are still a draft. They become readable once the host or the secretary signs them.";
    public const string ErrorUnauthorizedManage = "Only the meeting host can draw up or sign the minutes.";
    public const string ErrorMeetingNotEnded = "Minutes can only be drawn up once the meeting has ended.";
    public const string ErrorApprovedIsImmutable =
        "Approved minutes cannot be edited. Issue a revision instead — the signed version stays on record.";
    public const string ErrorNotApproved = "Only approved minutes can be revised.";
    public const string ErrorSignBeforeApprove = "The secretary must sign the minutes before the chair approves them.";
    public const string ErrorContentUnreadable =
        "This minutes document could not be read. Edit and save it before exporting.";
    public const string ErrorNumberCollision =
        "Another minutes document took that number a moment ago. Try again.";
    public const string ErrorRevisionAlreadyOpen =
        "Somebody else has just opened a revision of these minutes. Reload to see it.";

    // ------------------------------------------------------------------ sharing

    /// <summary>Only the people named on the share list may open the link. The default.</summary>
    public const string ShareModeInvitedOnly = "INVITED_ONLY";

    /// <summary>Anybody holding the URL may open it, signed in or not.</summary>
    public const string ShareModeAnyoneWithLink = "ANYONE_WITH_LINK";

    public const string ErrorShareLinkNotFound = "This share link is no longer available.";
    public const string ErrorShareModeUnknown =
        "Unknown sharing mode. Use INVITED_ONLY or ANYONE_WITH_LINK.";
    public const string ErrorShareEmailInvalid = "That does not look like an email address.";
    public const string ErrorShareDownloadDisabled =
        "The person who shared this document turned downloads off.";
    public const string ErrorSharePdfUnavailable =
        "PDF conversion is unavailable right now. The Word file still downloads.";
}
