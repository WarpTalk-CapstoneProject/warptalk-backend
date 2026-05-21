using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface ISubscriptionService
{
    Task<Result<SubscriptionDto>> GetActiveSubscriptionAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default);

    Task<Result<SubscriptionDto>> CreateSubscriptionAsync(
        CreateSubscriptionRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<SubscriptionDto>> CancelSubscriptionAsync(
        Guid workspaceId,
        string? reason,
        CancellationToken cancellationToken = default);
}
