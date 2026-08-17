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

    /// <summary>
    /// Manual admin adjustment against the workspace's active subscription. The long-standing
    /// AdjustCreditsAsync on the implementation was subscription-keyed and had no route at all —
    /// the admin portal's Adjust Credit button 404'd against it for weeks. This is the
    /// workspace-keyed door that button actually needs.
    /// </summary>
    Task<Result<CreditTransactionDto>> AdjustWorkspaceCreditsAsync(
        Guid workspaceId,
        AdjustCreditsRequest request,
        Guid adminUserId,
        CancellationToken cancellationToken = default);


    Task<Result<PaginatedResponse<CreditTransactionDto>>> GetCreditHistoryAsync(Guid workspaceId, CreditHistoryQuery query, CancellationToken cancellationToken = default);
    Task<Result<PaginatedResponse<CreditTransactionDto>>> GetGlobalCreditHistoryAsync(CreditHistoryQuery query, CancellationToken cancellationToken = default);

}
