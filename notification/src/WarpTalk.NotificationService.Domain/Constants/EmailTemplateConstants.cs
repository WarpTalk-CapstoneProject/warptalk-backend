namespace WarpTalk.NotificationService.Domain.Constants;

public static class EmailTemplateConstants
{
    /// <summary>notification_templates.channel for the transactional email templates.</summary>
    public const string ChannelEmail = "EMAIL";

    public const string ActionSaved = "SAVED";
    public const string ActionRestored = "RESTORED";
    public const string ActionReset = "RESET";

    public const int MaxNoteLength = 500;
}
