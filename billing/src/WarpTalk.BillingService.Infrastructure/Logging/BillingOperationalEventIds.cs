using Microsoft.Extensions.Logging;

namespace WarpTalk.BillingService.Infrastructure.Logging;

public static class BillingOperationalEventIds
{
    public static readonly EventId SettlementFailed = new(4101, nameof(SettlementFailed));
    public static readonly EventId BillingEventSkippedMissingRate = new(4102, nameof(BillingEventSkippedMissingRate));
    public static readonly EventId BillingCycleClosed = new(4103, nameof(BillingCycleClosed));
    public static readonly EventId InvoiceOverdueSuspend = new(4104, nameof(InvoiceOverdueSuspend));
    public static readonly EventId AiServiceSuspended = new(4105, nameof(AiServiceSuspended));
    public static readonly EventId AiServiceResumed = new(4106, nameof(AiServiceResumed));
}
