using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface ICreditService
{
    Task<Result<Guid>> StartSessionAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<Result<bool>> ProcessHeartbeatAsync(Guid sessionId, Guid workspaceId, CancellationToken cancellationToken = default);

    Task<Result<CreditBalanceDto>> GetWorkspaceCreditsAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<Result<CreditTransactionDto>> ConsumeCreditsAsync(Guid workspaceId, ConsumeCreditsRequest request, CancellationToken cancellationToken = default);
    Task<Result<CreditBalanceDto>> TopUpCreditsAsync(Guid workspaceId, TopUpRequest request, CancellationToken cancellationToken = default);
    Task<Result<object>> SimulatePaymentAsync(Guid workspaceId, decimal amount, string currency, CancellationToken cancellationToken = default);

    Task<Result<PaginatedResponse<CreditTransactionDto>>> GetCreditHistoryAsync(Guid workspaceId, CreditHistoryQuery query, CancellationToken cancellationToken = default);
    Task<Result<PaginatedResponse<CreditTransactionDto>>> GetGlobalCreditHistoryAsync(CreditHistoryQuery query, CancellationToken cancellationToken = default);

    Task<Result<CreditReservationDto>> ReserveCreditsAsync(ReserveCreditsRequest request, CancellationToken cancellationToken = default);
    Task<Result<CreditTransactionDto>> ConfirmConsumeAsync(Guid workspaceId, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<Result<bool>> RefundReservationAsync(Guid workspaceId, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<Result<CreditTransactionDto>> AdjustCreditsAsync(
        Guid workspaceId,
        AdjustCreditsRequest request,
        CancellationToken cancellationToken = default);
}
