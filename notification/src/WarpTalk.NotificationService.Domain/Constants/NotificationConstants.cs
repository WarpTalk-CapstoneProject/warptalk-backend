namespace WarpTalk.NotificationService.Domain.Constants;

public static class NotificationConstants
{
    // General
    public const string DefaultNotificationType = TypeSystem;
    
    // Notification Types
    public const string TypeSystemAlert = "SYSTEM_ALERT";
    public const string TypeMeetingInvite = "MEETING_INVITE";

    // Admin Notification Types (WT-58)
    public const string TypePromotion = "PROMOTION";
    public const string TypeSystem = "SYSTEM";
    public const string TypeAnnouncement = "ANNOUNCEMENT";
    public const string TypeMaintenance = "MAINTENANCE";

    // Target Audience Modes
    public const string TargetModeBroadcast = "BROADCAST";
    public const string TargetModeSegment = "SEGMENT";
    public const string TargetModeSpecificUsers = "SPECIFIC_USERS";

    // Lifecycle Statuses
    public const string StatusPending = "Pending";
    public const string StatusSent = "Sent";
    public const string StatusFailed = "Failed";

    // Pagination limits
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    // Redis Channels
    public const string RedisNewNotificationChannel = "warptalk:notifications:new";

    // Error Messages
    public const string ErrorPreferencesNotFound = "Preferences not found";
    public const string ErrorNotificationNotFound = "Notification not found";

    // Validation Error Codes
    public const string ErrorHtmlNotAllowed = "HTML_NOT_ALLOWED";
    public const string ErrorUnsupportedPayloadField = "UNSUPPORTED_PAYLOAD_FIELD";
    public const string ErrorInvalidFieldType = "INVALID_FIELD_TYPE";
    public const string ErrorMissingRequiredFields = "MISSING_REQUIRED_FIELDS";
}
