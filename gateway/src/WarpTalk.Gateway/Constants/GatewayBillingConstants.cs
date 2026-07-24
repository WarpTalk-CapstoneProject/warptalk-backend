namespace WarpTalk.Gateway.Constants;

public static class GatewayBillingConstants
{
    public const string NotificationChannel = "warptalk:notifications:new";
    public const string NotificationTypePrefix = "billing.";
    public const string HubPath = "/hubs/billing";
    public const string UserGroupTemplate = "user:{0}";
    public const string BillingNotificationEvent = "BillingNotification";
    public const string ProcessRedisNotificationError = "Failed to process incoming Redis billing notification message.";
    public const string BroadcastLogTemplate = "BillingRedisSubscriber: Broadcasted {EventName} ({Type}) to {GroupName}";
    public const string SubscriberStartedTemplate = "BillingRedisSubscriberService started listening to {Channel}.";
}
