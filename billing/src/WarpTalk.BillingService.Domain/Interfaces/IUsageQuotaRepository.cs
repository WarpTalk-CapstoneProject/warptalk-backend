using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface IUsageQuotaRepository
{
    Task<UsageQuota?> GetByWorkspaceIdAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task UpdateAsync(UsageQuota quota, CancellationToken cancellationToken = default);
    Task AddAsync(UsageQuota quota, CancellationToken cancellationToken = default);
}
