using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IPaymentService
{
    Task<Result<PaginatedResponse<PaymentTransactionDto>>> GetPaymentHistoryAsync(Guid workspaceId, PaginationQuery query, CancellationToken cancellationToken = default);
    Task<Result<PaymentTransactionDto>> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken = default);

}
