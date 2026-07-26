namespace WarpTalk.BillingService.Domain.Constants;

public static class RedisConstants
{
    public static class Keys
    {
        public const string ReservationZSet = "warptalk:billing:reservations_zset";
        public const string ReservationHash = "warptalk:billing:reservations_hash";
        public const string SessionZSet = "warptalk:billing:sessions_zset";
        public const string TempUsageLogList = "warptalk:billing:temp_usage_logs";
        public const string WorkspaceAiServiceStateTemplate = "workspace:{0}:ai_service_state";
        public const string WorkspaceAiServiceSuspendedTemplate = "workspace:{0}:ai_service_suspended";
        public const string TranslationRoomAiServiceStateTemplate = "translationRoom:{0}:ai_service_state";
        public const string TranslationRoomAiServiceSuspendedTemplate = "translationRoom:{0}:ai_service_suspended";
        public const string WorkspaceQuotaExceededTemplate = "workspace:{0}:quota:exceeded";
    }

    public static class Values
    {
        public const string True = "true";
        public const string False = "false";
    }
}
