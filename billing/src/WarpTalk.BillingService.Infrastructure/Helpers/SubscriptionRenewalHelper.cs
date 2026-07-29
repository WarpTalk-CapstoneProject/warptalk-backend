using System;
using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Infrastructure.Helpers;

public static class SubscriptionRenewalHelper
{
    public static (DateTime NewStart, DateTime NewEnd) CalculateNextCycleDates(
        DateTime currentPeriodEnd,
        string billingCycle)
    {
        var newStart = currentPeriodEnd;
        var newEnd = billingCycle switch
        {
            SubscriptionConstants.BillingCycles.Monthly => newStart.AddMonths(1),
            SubscriptionConstants.BillingCycles.Yearly => newStart.AddYears(1),
            _ => newStart.AddMonths(1)
        };
        return (newStart, newEnd);
    }
}
