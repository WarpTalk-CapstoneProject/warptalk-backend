namespace WarpTalk.Gateway.Constants;

public static class RealtimeConstants
{
    public static class RedisChannels
    {
        public const string NotificationsNew = "warptalk:notifications:new";
        public const string MeetingsEvents = "warptalk:meetings:events";
        public const string MeetingStarted = "meeting.started";
        public const string WorkspaceEvents = "warptalk:workspace:events";
        public const string DocumentsEvents = "warptalk:documents:events";
        public const string TranslationRoomCommands = "warptalk:translation-room:commands";
        public const string ParticipantOffline = "translationRoom:participant-offline";
    }

    public static class ClientMethods
    {
        public const string NewNotification = "NewNotification";
        public const string NotificationRead = "NotificationRead";
        public const string AllNotificationsRead = "AllNotificationsRead";

        public const string MeetingEvent = "MeetingEvent";
        public const string MeetingStarted = "MeetingStarted";
        public const string MeetingStatusChanged = "MeetingStatusChanged";

        public const string WorkspaceEvent = "WorkspaceEvent";
        public const string UserPresenceChanged = "UserPresenceChanged";
        public const string WorkspaceSettingsUpdated = "WorkspaceSettingsUpdated";
        public const string UserProfileUpdated = "UserProfileUpdated";

        public const string DocumentStatusChanged = "DocumentStatusChanged";
        public const string DocumentCommentAdded = "DocumentCommentAdded";
        public const string DocumentDeleted = "DocumentDeleted";

        public const string ReactionReceived = "ReactionReceived";
        public const string CollaborativeNoteUpdated = "CollaborativeNoteUpdated";
        public const string ParticipantAdmitted = "ParticipantAdmitted";
    }

    public static class Groups
    {
        public static string User(string userId) => $"user:{userId}";
        public static string Workspace(string workspaceId) => $"workspace:{workspaceId}";
        public static string TranslationRoom(string roomId) => $"translationRoom:{roomId}";
    }
}
