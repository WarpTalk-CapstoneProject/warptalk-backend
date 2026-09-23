namespace WarpTalk.TranslationRoomService.Domain.Constants;

public static class MeetingActionItemConstants
{
    public const string StatusOpen = "OPEN";
    public const string StatusDone = "DONE";
    /// <summary>Decided against, rather than completed. A record that only ever closes as DONE lies.</summary>
    public const string StatusDropped = "DROPPED";

    /// <summary>Materialised from an approved biên bản.</summary>
    public const string SourceMinutes = "MINUTES";
    /// <summary>Asked for in chat and created by WarpBot with the caller's own token.</summary>
    public const string SourceAssistant = "ASSISTANT";

    /// <summary>`task` is TEXT; this bounds what one chat request can write into somebody's list.</summary>
    public const int MaxTaskLength = 1000;
    /// <summary>Matches owner_name VARCHAR(200).</summary>
    public const int MaxOwnerNameLength = 200;

    public const string ErrorTaskRequired = "An action item needs a task.";
    public const string ErrorTaskTooLong = "An action item's task must be 1000 characters or fewer.";
    public const string ErrorOwnerNameTooLong = "An owner's name must be 200 characters or fewer.";
    public const string ErrorOwnerUnresolved =
        "Nobody in this meeting matches that name exactly once. Name a participant, assign it to yourself, or leave it unassigned.";

    public const string ErrorActionItemNotFound = "That action item does not exist.";
    public const string ErrorUnauthorizedClose =
        "Only the person the task was assigned to, or the meeting host, can change it.";
    public const string ErrorInvalidStatus = "An action item is OPEN, DONE or DROPPED.";
}
