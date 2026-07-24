namespace WarpTalk.BillingService.Domain.Constants;

public static class RedisConstants
{
    public static class Keys
    {
        public const string ReservationZSet = "warptalk:billing:reservations_zset";
        public const string ReservationHash = "warptalk:billing:reservations_hash";
        public const string SessionZSet = "warptalk:billing:sessions_zset";
        public const string TempUsageLogList = "warptalk:billing:temp_usage_logs";
    }
}
