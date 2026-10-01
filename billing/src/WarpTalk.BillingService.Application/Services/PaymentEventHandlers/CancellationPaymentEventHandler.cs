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
            // WT-878: stamped so this cancellation (a refund, a cancelled payment) is told apart
            // from a pre-WT-878 cancel-at-period-end, which kept the plan in force until the period
            // ended and left cancelled_at null (Subscription.IsLegacyCancelledInPeriod).
            context.Subscription.CancelledAt ??= DateTime.UtcNow;
            context.Subscription.UpdatedAt = DateTime.UtcNow;
        }

        return Task.FromResult(Result.Success());
    }
}
