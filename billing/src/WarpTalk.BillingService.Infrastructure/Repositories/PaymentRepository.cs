using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class PaymentRepository : GenericRepository<Payment>, IPaymentRepository
{
    public PaymentRepository(BillingDbContext context) : base(context)
    {
    }

    public async Task<Payment?> GetWithSubscriptionAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        return await _dbSet
            .Include(p => p.Subscription)
            .FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);
    }

    public async Task<Payment?> GetWithSubscriptionAndPlanAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        return await _dbSet
            .Include(p => p.Subscription)
                .ThenInclude(s => s.Plan)
            .FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);
    }
}
