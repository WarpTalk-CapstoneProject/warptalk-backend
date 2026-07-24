namespace WarpTalk.BillingService.Infrastructure.Options;

public sealed class BillingWorkerOptions
{
    public const string SectionName = "Billing:Workers";

    public int SessionMonitorIntervalSeconds { get; set; }
    public int StaleReservationIntervalMinutes { get; set; }
    public int SubscriptionExpirationIntervalMinutes { get; set; }
    public int SubscriptionRenewalIntervalMinutes { get; set; }
    public int SubscriptionRenewalWindowHours { get; set; }
    public int SubscriptionRenewalLookbackHours { get; set; }
    public int DailyAuditHourUtc { get; set; }
    public int BillingAggregationIntervalMinutes { get; set; }
    public int BillingAggregationBatchSize { get; set; }

    public TimeSpan SessionMonitorInterval => TimeSpan.FromSeconds(SessionMonitorIntervalSeconds);
    public TimeSpan StaleReservationInterval => TimeSpan.FromMinutes(StaleReservationIntervalMinutes);
    public TimeSpan SubscriptionExpirationInterval => TimeSpan.FromMinutes(SubscriptionExpirationIntervalMinutes);
    public TimeSpan SubscriptionRenewalInterval => TimeSpan.FromMinutes(SubscriptionRenewalIntervalMinutes);
    public TimeSpan SubscriptionRenewalWindow => TimeSpan.FromHours(SubscriptionRenewalWindowHours);
    public TimeSpan SubscriptionRenewalLookback => TimeSpan.FromHours(SubscriptionRenewalLookbackHours);
    public TimeSpan BillingAggregationInterval => TimeSpan.FromMinutes(BillingAggregationIntervalMinutes);
}
