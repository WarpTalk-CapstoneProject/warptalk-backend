namespace WarpTalk.NotificationService.Domain.Constants;

public static class AnnouncementConstants
{
    // Stored statuses.
    public const string StatusDraft = "DRAFT";
    public const string StatusPublished = "PUBLISHED";
    public const string StatusArchived = "ARCHIVED";

    // Effective statuses: what the admin list shows and filters by. SCHEDULED and ENDED are a
    // PUBLISHED row read against the clock.
    public const string EffectiveDraft = StatusDraft;
    public const string EffectiveScheduled = "SCHEDULED";
    public const string EffectivePublished = StatusPublished;
    public const string EffectiveEnded = "ENDED";
    public const string EffectiveArchived = StatusArchived;

    public static readonly string[] EffectiveStatuses =
        [EffectiveDraft, EffectiveScheduled, EffectivePublished, EffectiveEnded, EffectiveArchived];

    public const string TypeAnnouncement = "ANNOUNCEMENT";
    public const string TypeFeature = "FEATURE";
    public const string TypeMaintenance = "MAINTENANCE";
    public const string TypePromotion = "PROMOTION";

    public static readonly string[] Types = [TypeAnnouncement, TypeFeature, TypeMaintenance, TypePromotion];

    public const string AudienceAll = "ALL";
    public const string AudiencePlans = "PLANS";
    public const string AudienceWorkspaces = "WORKSPACES";

    public static readonly string[] AudienceModes = [AudienceAll, AudiencePlans, AudienceWorkspaces];

    public const string PlacementTopBanner = "TOP_BANNER";
    public const string PlacementModal = "MODAL";
    public const string PlacementToast = "TOAST";
    public const string PlacementNotificationCenter = "NOTIFICATION_CENTER";
    public const string PlacementDashboardCard = "DASHBOARD_CARD";

    public static readonly string[] Placements =
        [PlacementTopBanner, PlacementModal, PlacementToast, PlacementNotificationCenter, PlacementDashboardCard];

    public static readonly string[] Variants = ["SUBTLE", "SOLID", "OUTLINE"];

    public static readonly string[] AccentColors = ["BRAND", "BLUE", "GREEN", "AMBER", "RED", "VIOLET", "NEUTRAL"];

    /// <summary>The icon set the web app can draw. Kept here so the API refuses a name it cannot render.</summary>
    public static readonly string[] Icons =
        ["megaphone", "sparkle", "rocket", "wrench", "warning", "gift", "info", "calendar", "lightning", "star", "bell", "heart"];

    public const string FrequencyUntilDismissed = "UNTIL_DISMISSED";
    public const string FrequencyOnce = "ONCE";
    public const string FrequencyEverySession = "EVERY_SESSION";
    public const string FrequencyDaily = "DAILY";

    public static readonly string[] Frequencies =
        [FrequencyUntilDismissed, FrequencyOnce, FrequencyEverySession, FrequencyDaily];

    public const string EventImpression = "IMPRESSION";
    public const string EventDismiss = "DISMISS";
    public const string EventCtaClick = "CTA_CLICK";
    public const string EventSecondaryClick = "SECONDARY_CLICK";

    public static readonly string[] Events = [EventImpression, EventDismiss, EventCtaClick, EventSecondaryClick];

    public static readonly string[] Roles = ["Owner", "Admin", "Member"];

    public static readonly string[] Locales = ["en", "vi", "ja"];

    public const int MaxPriority = 100;
    public const int MaxNewUserDays = 365;
    public const int MaxSessionIdLength = 64;
    public const int MaxImageUrlLength = 2048;
    public const int MaxAssetBytes = 2 * 1024 * 1024;

    public static readonly string[] AssetContentTypes = ["image/png", "image/jpeg", "image/webp", "image/gif"];

    public const int MaxBulkItems = 100;

    public const int MaxTitleLength = 200;
    public const int MaxBodyLength = 20_000;
    public const int MaxCtaLabelLength = 60;
    public const int MaxCtaUrlLength = 2048;
    public const int MaxAudienceEntries = 200;
    public const int MaxPlanSlugLength = 80;

    /// <summary>How many live announcements one person is shown at most.</summary>
    public const int MaxActiveForViewer = 10;
}
