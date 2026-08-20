namespace WarpTalk.TranslationRoomService.Domain.Constants;

public static class MeetingMinutesConstants
{
    public const string StatusDraft = "DRAFT";
    public const string StatusInReview = "IN_REVIEW";
    public const string StatusApproved = "APPROVED";

    public const string ErrorRoomNotFound = "Meeting not found.";
    public const string ErrorMinutesNotFound = "This meeting has no minutes yet.";
    public const string ErrorUnauthorizedRead = "You do not have access to this meeting.";
    public const string ErrorUnauthorizedManage = "Only the meeting host can draw up or sign the minutes.";
    public const string ErrorMeetingNotEnded = "Minutes can only be drawn up once the meeting has ended.";
    public const string ErrorApprovedIsImmutable =
        "Approved minutes cannot be edited. Issue a revision instead — the signed version stays on record.";
    public const string ErrorNotApproved = "Only approved minutes can be revised.";
    public const string ErrorSignBeforeApprove = "The secretary must sign the minutes before the chair approves them.";
    public const string ErrorNumberCollision =
        "Another minutes document took that number a moment ago. Try again.";
}
