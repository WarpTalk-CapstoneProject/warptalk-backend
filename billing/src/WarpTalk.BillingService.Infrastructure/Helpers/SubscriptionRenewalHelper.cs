using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Infrastructure.Helpers;

public static class SubscriptionRenewalHelper
{
    public static async Task RenewOneAsync(
        IUnitOfWork unitOfWork,
        Subscription subscription,
        CancellationToken cancellationToken)
    {
        var plan = subscription.Plan;
        var creditsToAdd = plan.CreditsPerCycle;
        var (newStart, newEnd) = CalculateNextCycleDates(subscription.CurrentPeriodEnd, plan.BillingCycle);

        subscription.ApplyCycleAllocation(creditsToAdd);
        subscription.CreditsUsedThisCycle = 0;
        subscription.CurrentPeriodStart = newStart;
        subscription.CurrentPeriodEnd = newEnd;
        subscription.UpdatedAt = DateTime.UtcNow;

        unitOfWork.SubscriptionRepository.Update(subscription);

        var renewalTx = subscription.CreateRenewalTransaction(plan, newStart);
        await unitOfWork.CreditTransactionRepository.AddAsync(renewalTx, cancellationToken);
    }

    public static (DateTime NewStart, DateTime NewEnd) CalculateNextCycleDates(
        DateTime currentPeriodEnd,
        string billingCycle)
    {
        var newStart = currentPeriodEnd;
        var newEnd = billingCycle switch
        {
            SubscriptionConstants.BillingCycles.Monthly => newStart.AddMonths(1),
            SubscriptionConstants.BillingCycles.Semiannual => newStart.AddMonths(6),
            SubscriptionConstants.BillingCycles.Yearly => newStart.AddYears(1),
            _ => newStart.AddMonths(1)
        };
        return (newStart, newEnd);
    }
}
