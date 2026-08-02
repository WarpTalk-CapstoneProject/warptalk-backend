using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Helpers;

public static class SubscriptionHelper
{
    public static async Task<Result<Subscription>> GetActiveSubscriptionAsync(
        IUnitOfWork unitOfWork,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var sub = await unitOfWork.SubscriptionRepository.GetActiveByWorkspaceIdAsync(workspaceId, cancellationToken: cancellationToken);
        if (sub is null)
            return Result.Failure<Subscription>(
                ApiMessageConstants.ErrorMessages.BillingSubscriptionNotFound,
                ErrorCodes.BillingSubscriptionNotFound);

        return Result.Success(sub);
    }
}
