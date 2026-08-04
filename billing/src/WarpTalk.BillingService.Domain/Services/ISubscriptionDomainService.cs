using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Services;

public interface ISubscriptionDomainService
{
    bool ConsumeCredits(Subscription subscription, int amount);
    void RenewCycle(Subscription subscription);
}
