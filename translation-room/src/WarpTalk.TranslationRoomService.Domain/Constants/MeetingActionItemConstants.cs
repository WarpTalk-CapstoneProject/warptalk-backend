namespace WarpTalk.TranslationRoomService.Domain.Constants;

public static class MeetingActionItemConstants
{
    public const string StatusOpen = "OPEN";
    public const string StatusDone = "DONE";
    /// <summary>Decided against, rather than completed. A record that only ever closes as DONE lies.</summary>
    public const string StatusDropped = "DROPPED";

    public const string ErrorActionItemNotFound = "That action item does not exist.";
    public const string ErrorUnauthorizedClose =
        "Only the person the task was assigned to, or the meeting host, can change it.";
    public const string ErrorInvalidStatus = "An action item is OPEN, DONE or DROPPED.";
}
