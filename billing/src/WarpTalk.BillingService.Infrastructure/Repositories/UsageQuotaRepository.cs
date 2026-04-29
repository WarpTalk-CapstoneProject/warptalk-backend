using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class UsageQuotaRepository : IUsageQuotaRepository
{
    private readonly BillingDbContext _dbContext;

    public UsageQuotaRepository(BillingDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<UsageQuota?> GetByWorkspaceIdAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.UsageQuotas
            .Include(q => q.Plan)
            .FirstOrDefaultAsync(q => q.WorkspaceId == workspaceId, cancellationToken);
    }

    public Task UpdateAsync(UsageQuota quota, CancellationToken cancellationToken = default)
    {
        _dbContext.UsageQuotas.Update(quota);
        return Task.CompletedTask;
    }

    public async Task AddAsync(UsageQuota quota, CancellationToken cancellationToken = default)
    {
        await _dbContext.UsageQuotas.AddAsync(quota, cancellationToken);
    }
}
