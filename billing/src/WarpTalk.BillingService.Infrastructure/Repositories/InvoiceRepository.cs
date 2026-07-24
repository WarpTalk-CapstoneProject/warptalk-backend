using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class InvoiceRepository : GenericRepository<Invoice>, IInvoiceRepository
{
    public InvoiceRepository(BillingDbContext context) : base(context)
    {
    }

    public async Task<PagedResult<Invoice>> GetPageAsync(PageRequest page, Guid? workspaceId, CancellationToken cancellationToken = default)
    {
        var normalized = RepositoryPaging.Normalize(page);
        var filtered = workspaceId.HasValue
            ? _dbSet.Where(i => i.Payment.Subscription.WorkspaceId == workspaceId.Value)
            : _dbSet.AsQueryable();

        var total = await filtered.CountAsync(cancellationToken);
        var items = await filtered
            .Include(i => i.Payment)
            .ThenInclude(p => p.Subscription)
            .OrderByDescending(i => i.CreatedAt)
            .Skip(normalized.Skip)
            .Take(normalized.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<Invoice>(items, total, normalized.PageNumber, normalized.PageSize);
    }
}
