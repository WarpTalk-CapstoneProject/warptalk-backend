using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Mappers;

public static class PaymentEventContextMapper
{
    public static PaymentEventContext ToPaymentEventContext(
        this StripePaymentEventRequest request,
        Guid workspaceId,
        Guid userId,
        string providerTransactionId,
        string parsedPaymentStatus,
        Payment? existingPayment,
        Subscription? subscription)
        => new(
            request,
            workspaceId,
            userId,
            providerTransactionId,
            parsedPaymentStatus,
            existingPayment?.Id ?? Guid.NewGuid(),
            existingPayment,
            subscription);
}
