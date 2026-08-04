using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.Shared.Contracts.Admin;
using WarpTalk.BillingService.Application.DTOs;

namespace WarpTalk.BillingService.Application.Interfaces;

/// <summary>
/// Per-workspace analytics and credit ledger history for the System Admin portal (WT-206).
/// Every method takes the workspace explicitly and filters on it; there is no ambient tenant.
/// Read-only — the ledger is never written from here.
/// </summary>
public interface IAdminWorkspaceAnalyticsService
{
    Task<Result<AdminWorkspaceAnalyticsDto>> GetAnalyticsAsync(
        Guid workspaceId,
        AdminDateRange range,
        CancellationToken ct = default);

    Task<Result<AdminPagedResult<AdminCreditTransactionDto>>> GetCreditTransactionsAsync(
        Guid workspaceId,
        AdminCreditTransactionQuery query,
        CancellationToken ct = default);
}
