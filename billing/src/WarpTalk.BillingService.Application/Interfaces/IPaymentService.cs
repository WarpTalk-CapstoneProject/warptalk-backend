using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IPaymentService
{
    Task<Result<PagedResult<PaymentTransactionDto>>> GetPaymentHistoryAsync(
        Guid workspaceId,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<Result<PaymentTransactionDto>> CreatePaymentAsync(
        Guid subscriptionId,
        Guid userId,
        decimal amount,
        decimal taxAmount,
        string currency,
        string paymentMethod,
        string provider,
        CancellationToken cancellationToken = default);

    Task<Result<PaymentTransactionDto>> UpdatePaymentStatusAsync(
        Guid paymentId,
        string status,
        string? providerTransactionId,
        string? failureReason,
        CancellationToken cancellationToken = default);
}
