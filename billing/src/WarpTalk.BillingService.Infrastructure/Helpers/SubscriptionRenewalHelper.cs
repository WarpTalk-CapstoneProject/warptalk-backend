using System;

namespace WarpTalk.BillingService.Infrastructure.Helpers;

public static class SubscriptionRenewalHelper
{
    public static (DateTime NewStart, DateTime NewEnd) CalculateNextCycleDates(
        DateTime currentPeriodEnd,
        string _)
    {
        var newStart = currentPeriodEnd;
        var newEnd = newStart.AddMonths(1);
        return (newStart, newEnd);
    }
}
