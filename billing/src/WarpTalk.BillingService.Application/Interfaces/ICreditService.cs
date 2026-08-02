using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface ICreditService
{
    Task<Result<CreditBalanceDto>> GetWorkspaceCreditsAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<Result<CreditTransactionDto>> ConsumeCreditsDirectlyAsync(Guid workspaceId, ConsumeCreditsRequest request, CancellationToken cancellationToken = default);
    Task<Result<CreditBalanceDto>> TopUpCreditsAsync(Guid workspaceId, TopUpRequest request, CancellationToken cancellationToken = default);

    Task<Result<PaginatedResponse<CreditTransactionDto>>> GetCreditHistoryAsync(Guid workspaceId, CreditHistoryQuery query, CancellationToken cancellationToken = default);
    Task<Result<PaginatedResponse<CreditTransactionDto>>> GetGlobalCreditHistoryAsync(CreditHistoryQuery query, CancellationToken cancellationToken = default);

    Task<Result<CreditTransactionDto>> ManualAdjustCreditsAsync(
        Guid workspaceId,
        ManualAdjustCreditsRequest request,
        CancellationToken cancellationToken = default);
}
