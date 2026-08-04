using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface IPaymentRepository : IGenericRepository<Payment>
{
    Task<Payment?> GetWithSubscriptionAsync(Guid paymentId, CancellationToken cancellationToken);
    Task<Payment?> GetWithSubscriptionAndPlanAsync(Guid paymentId, CancellationToken cancellationToken);
    Task<PagedResult<Payment>> GetHistoryPageAsync(Guid subscriptionId, PageRequest page, CancellationToken cancellationToken = default);
}
