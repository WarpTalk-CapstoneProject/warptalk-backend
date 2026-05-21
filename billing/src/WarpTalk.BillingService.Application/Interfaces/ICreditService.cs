using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface ICreditService
{
    Task<Result<CreditBalanceDto>> GetWorkspaceCreditsAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default);

    Task<Result<CreditTransactionDto>> ConsumeCreditsAsync(
        Guid workspaceId,
        ConsumeCreditsRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<CreditBalanceDto>> TopUpCreditsAsync(
        Guid workspaceId,
        TopUpRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<PagedResult<CreditTransactionDto>>> GetCreditHistoryAsync(
        Guid workspaceId,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task TakeSnapshotAsync(Guid subscriptionId, CancellationToken cancellationToken = default);
}
