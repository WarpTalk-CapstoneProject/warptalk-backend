using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface ICreditService
{
    /// <summary>
    /// Credit spend broken down by member, so a workspace owner can see who is consuming what.
    ///
    /// WT-413. Read from usage_records, which already carries UserId, WorkspaceId and
    /// CreditsConsumed — no new capture and no migration. Members are returned by id; this
    /// service has no user directory, and the caller already has the member list on screen.
    /// </summary>
    Task<Result<WorkspaceUsageByMemberDto>> GetUsageByMemberAsync(
        Guid workspaceId,
        DateTime? from,
        DateTime? to,
        CancellationToken cancellationToken = default);

    Task<Result<CreditBalanceDto>> GetWorkspaceCreditsAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<Result<CreditTransactionDto>> ConsumeCreditsDirectlyAsync(Guid workspaceId, ConsumeCreditsRequest request, CancellationToken cancellationToken = default);


    Task<Result<PaginatedResponse<CreditTransactionDto>>> GetCreditHistoryAsync(Guid workspaceId, CreditHistoryQuery query, CancellationToken cancellationToken = default);
    Task<Result<PaginatedResponse<CreditTransactionDto>>> GetGlobalCreditHistoryAsync(CreditHistoryQuery query, CancellationToken cancellationToken = default);

}
