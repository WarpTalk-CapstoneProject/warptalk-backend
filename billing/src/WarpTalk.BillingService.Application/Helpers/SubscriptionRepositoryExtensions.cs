using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Application.Helpers;

public static class SubscriptionRepositoryExtensions
{
    public static Task<Subscription?> GetActiveByWorkspaceIdAsync(
        this ISubscriptionRepository repository,
        Guid workspaceId,
        bool requireActivePeriod = false,
        CancellationToken cancellationToken = default)
    {
        return repository.FirstOrDefaultAsync(
            s => s.WorkspaceId == workspaceId && s.IsActive && s.DeletedAt == null &&
                 (!requireActivePeriod || s.CurrentPeriodEnd >= DateTime.UtcNow),
            cancellationToken);
    }
}
