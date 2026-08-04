using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services.PaymentEventHandlers;

public sealed class CancellationPaymentEventHandler : IPaymentEventHandler
{
    public bool CanHandle(PaymentEventContext context)
        => context.ParsedPaymentStatus is PaymentConstants.PaymentStatuses.Cancelled or PaymentConstants.PaymentStatuses.Refunded;

    public Task<Result> HandleAsync(PaymentEventContext context, CancellationToken cancellationToken = default)
    {
        if (context.Subscription is not null)
        {
            context.Subscription.Status = SubscriptionConstants.SubscriptionStatuses.Cancelled;
            context.Subscription.AutoRenew = false;
            context.Subscription.UpdatedAt = DateTime.UtcNow;
        }

        return Task.FromResult(Result.Success());
    }
}
