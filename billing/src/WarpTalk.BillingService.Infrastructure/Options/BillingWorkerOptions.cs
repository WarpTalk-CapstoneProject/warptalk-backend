namespace WarpTalk.BillingService.Infrastructure.Options;

public sealed class BillingWorkerOptions
{
    public const string SectionName = "Billing:Workers";

    public int SessionMonitorIntervalSeconds { get; set; }
    public int SubscriptionExpirationIntervalMinutes { get; set; }
    public int SubscriptionRenewalLookbackHours { get; set; }
    public int DailyAuditHourUtc { get; set; }
    public int BillingAggregationIntervalSeconds { get; set; }
    public int BillingAggregationBatchSize { get; set; }
    public int BillingCycleIntervalMinutes { get; set; } = 60;
    public int InvoiceOverdueIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// How often every workspace's entitlements are re-resolved and republished. 0 disables it.
    ///
    /// WT-430. Consumers enforce from a local snapshot that is only rewritten when billing
    /// publishes, and only three methods publish — all of them reacting to a mutation made THROUGH
    /// billing. A change that reaches the database any other way leaves every consumer enforcing a
    /// stale answer with nothing to notice: production ran for two days on platform-default quotas
    /// after a subscription's status changed, and the snapshot still read the value resolved before
    /// it. This is the sweep that closes that hole.
    /// </summary>
    public int EntitlementReconcileIntervalMinutes { get; set; } = 60;

    public TimeSpan EntitlementReconcileInterval => TimeSpan.FromMinutes(EntitlementReconcileIntervalMinutes);

    public TimeSpan SessionMonitorInterval => TimeSpan.FromSeconds(SessionMonitorIntervalSeconds);
    public TimeSpan SubscriptionExpirationInterval => TimeSpan.FromMinutes(SubscriptionExpirationIntervalMinutes);
    public TimeSpan SubscriptionRenewalLookback => TimeSpan.FromHours(SubscriptionRenewalLookbackHours);
    public TimeSpan BillingAggregationInterval => TimeSpan.FromSeconds(BillingAggregationIntervalSeconds);
    public TimeSpan BillingCycleInterval => TimeSpan.FromMinutes(BillingCycleIntervalMinutes);
    public TimeSpan InvoiceOverdueInterval => TimeSpan.FromMinutes(InvoiceOverdueIntervalMinutes);
}
