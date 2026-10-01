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

    // ── Custom templates (v3) ──

    public const string StatusDeleted = "DELETED";

    public const string CategoryTransactionalCustom = "TRANSACTIONAL_CUSTOM";
    public const string CategoryMarketing = "MARKETING";
    public const string CategoryAnnouncement = "ANNOUNCEMENT";

    public static readonly string[] Categories = [CategoryTransactionalCustom, CategoryMarketing, CategoryAnnouncement];

    public const string VariableText = "TEXT";
    public const string VariableUrl = "URL";
    public const string VariableDate = "DATE";
    public const string VariableNumber = "NUMBER";
    public const string VariableMultiline = "MULTILINE";

    public static readonly string[] VariableTypes = [VariableText, VariableUrl, VariableDate, VariableNumber, VariableMultiline];

    public const int MaxCustomVariables = 20;
    public const int MaxDeleteReasonLength = 500;

    /// <summary>Filled in by the sender for every recipient; a custom template may always use them.</summary>
    public const string VariableRecipientName = "RecipientName";
    public const string VariableRecipientEmail = "RecipientEmail";

    /// <summary>Filled in from the announcement when a template is sent as its email channel.</summary>
    public const string VariableAnnouncementTitle = "AnnouncementTitle";
    public const string VariableAnnouncementText = "AnnouncementText";
    public const string VariableAnnouncementLink = "AnnouncementLink";

    // ── Audience sends ──

    public const string CampaignSourceManual = "MANUAL";
    public const string CampaignSourceAnnouncement = "ANNOUNCEMENT";

    public const string CampaignQueued = "QUEUED";
    public const string CampaignSending = "SENDING";
    public const string CampaignCompleted = "COMPLETED";
    public const string CampaignCancelled = "CANCELLED";
    public const string CampaignFailed = "FAILED";

    public const string RecipientPending = "PENDING";
    public const string RecipientSent = "SENT";
    public const string RecipientFailed = "FAILED";
    public const string RecipientSkipped = "SKIPPED";
}
