namespace WarpTalk.NotificationService.Domain.Constants;

public static class NotificationConstants
{
    // General
    public const string DefaultNotificationType = TypeSystem;

    // Notification Types
    public const string TypeSystemAlert = "SYSTEM_ALERT";
    public const string TypeMeetingInvite = "MEETING_INVITE";
    /// <summary>
    /// The type translation-room ACTUALLY sends when somebody is invited to a room
    /// (TranslationRoomService.MeetingInvitedNotificationType). It is NOT
    /// <see cref="TypeMeetingInvite"/>: that constant is a different string ("MEETING_INVITE")
    /// with a different payload schema (meeting_id + inviter_name) and no producer anywhere.
    ///
    /// The past tense is what broke it. "MEETING_INVITED" was absent from the validator's schema
    /// table, so every invitation notification arrived as an UNKNOWN type carrying a payload and
    /// was rejected with UNSUPPORTED_NOTIFICATION_TYPE — the same failure MEETING_STARTED and
    /// MEETING_SUMMARY_READY had, and for the same reason. The producer does not read the reply,
    /// so it logged "invite_notification_sent" and nothing was ever created: zero MEETING_INVITED
    /// rows against 538 invitations, while its siblings persisted normally.
    /// </summary>
    public const string TypeMeetingInvited = "MEETING_INVITED";
    // WT-14: scheduled-meeting reminders (T-10min / T-1min), sent by the translation-room
    // service's ReminderNotificationWorker via SendNotification.
    public const string TypeMeetingReminder = "MEETING_REMINDER";
    // Both are PUBLISHED by translation-room — TranslationRoomService when a room goes live and
    // ArtifactsFinalizationWorker when a summary lands — and both were missing from the
    // validator's schema table. An unknown type carrying a payload is rejected outright, so
    // every "your meeting started" and every "your summary is ready" notification was dropped
    // at validation with UNSUPPORTED_NOTIFICATION_TYPE. Neither producer looks at the reply, so
    // nothing anywhere reported it; the only trace was a warning line in the notification
    // service's own log.
    public const string TypeMeetingStarted = "MEETING_STARTED";
    public const string TypeMeetingSummaryReady = "MEETING_SUMMARY_READY";

    /// <summary>
    /// Somebody was given work by an approved biên bản. Registered in the same commit as its
    /// producer, because this constant existing without a schema entry below is exactly how
    /// MEETING_STARTED, MEETING_SUMMARY_READY, MEETING_INVITED and WORKSPACE_ROLE_CHANGED each
    /// spent months being created, logged as sent, and discarded at validation.
    /// </summary>
    public const string TypeActionItemAssigned = "ACTION_ITEM_ASSIGNED";

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
