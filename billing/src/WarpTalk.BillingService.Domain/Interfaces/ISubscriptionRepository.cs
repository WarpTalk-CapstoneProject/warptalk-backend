using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface ISubscriptionRepository : IGenericRepository<Subscription>
{
    Task DeactivateOtherActiveSubscriptionsAsync(Guid userId, Guid excludeSubscriptionId, CancellationToken cancellationToken);
}
