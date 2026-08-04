using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface ICreditBalanceSnapshotRepository : IGenericRepository<CreditBalanceSnapshot>
{
    Task<CreditBalanceSnapshot?> GetLatestForSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default);
}
