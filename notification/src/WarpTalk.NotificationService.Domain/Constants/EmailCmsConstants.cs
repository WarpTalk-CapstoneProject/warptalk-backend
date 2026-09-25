namespace WarpTalk.NotificationService.Domain.Constants;

public static class EmailCmsConstants
{
    public const string KindLayout = "LAYOUT";
    public const string KindPartial = "PARTIAL";

    public static readonly string[] Kinds = [KindLayout, KindPartial];

    public const string StatusActive = "ACTIVE";
    public const string StatusArchived = "ARCHIVED";

    public const string OwnerContent = "CONTENT";
    public const string OwnerBlock = "BLOCK";

    public const string ActionPublished = "PUBLISHED";

    public const int MaxKeyLength = 60;
    public const int MaxNameLength = 120;
    public const int MaxDescriptionLength = 500;
    public const int MaxNoteLength = 500;
    public const int MaxSampleSets = 20;
    public const int MaxSampleValueLength = 2_000;
    public const int MaxTestRecipients = 5;
}
