using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IPaymentService
{
    Task<int> CalculateBalanceAsync(Guid subscriptionId, CancellationToken cancellationToken = default);

    Task<Result<PaginatedResponse<PaymentTransactionDto>>> GetPaymentHistoryAsync(Guid workspaceId, PaginationQuery query, CancellationToken cancellationToken = default);
    Task<Result<PaymentTransactionDto>> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken = default);
    Task<Result<PaymentTransactionDto>> UpdatePaymentStatusAsync(
        Guid paymentId,
        UpdatePaymentStatusRequest request,
        CancellationToken cancellationToken = default);
    Task<Result<bool>> HandleWebhookAsync(PaymentWebhookRequest request, CancellationToken cancellationToken = default);
}
