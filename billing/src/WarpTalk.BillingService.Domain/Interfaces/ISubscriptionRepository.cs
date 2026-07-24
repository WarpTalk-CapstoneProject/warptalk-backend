using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface ISubscriptionRepository : IGenericRepository<Subscription>
{
    Task DeactivateOtherActiveSubscriptionsAsync(Guid userId, Guid excludeSubscriptionId, CancellationToken cancellationToken);
    Task<PagedResult<Subscription>> GetPageAsync(PageRequest page, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Subscription>> GetActiveSubscriptionsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Subscription>> GetDueForRenewalAsync(DateTime renewalThreshold, DateTime lowerBound, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Subscription>> GetExpiredActiveSubscriptionsAsync(DateTime now, CancellationToken cancellationToken = default);
}
