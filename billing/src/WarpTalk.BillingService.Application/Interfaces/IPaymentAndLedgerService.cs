using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IPaymentAndLedgerService
{
    // --- Ledger Methods ---
    Task<int> CalculateBalanceAsync(Guid subscriptionId, CancellationToken cancellationToken = default);

    // --- Payment Methods ---
    Task<Result<PagedResult<PaymentTransactionDto>>> GetPaymentHistoryAsync(
        Guid workspaceId,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<Result<PaymentTransactionDto>> CreatePaymentAsync(
        CreatePaymentRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<PaymentTransactionDto>> UpdatePaymentStatusAsync(
        Guid paymentId,
        string status,
        string? providerTransactionId,
        string? failureReason,
        CancellationToken cancellationToken = default);

    Task<Result<bool>> HandleWebhookAsync(
        PaymentWebhookRequest request,
        CancellationToken cancellationToken = default);
}
