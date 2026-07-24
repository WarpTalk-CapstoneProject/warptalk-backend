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

    public async Task<PagedResult<Payment>> GetHistoryPageAsync(Guid subscriptionId, PageRequest page, CancellationToken cancellationToken = default)
    {
        var normalized = RepositoryPaging.Normalize(page);
        var filtered = _dbSet.Where(p => p.SubscriptionId == subscriptionId);

        var total = await filtered.CountAsync(cancellationToken);
        var items = await filtered
            .OrderByDescending(p => p.CreatedAt)
            .Skip(normalized.Skip)
            .Take(normalized.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<Payment>(items, total, normalized.PageNumber, normalized.PageSize);
    }
}
