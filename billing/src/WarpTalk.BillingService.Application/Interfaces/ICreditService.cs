using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface ICreditService
{
    // --- Session Heartbeat ---
    Task<Result<Guid>> StartSessionAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<Result<bool>> ProcessHeartbeatAsync(Guid sessionId, Guid workspaceId, CancellationToken cancellationToken = default);

    // --- Credit & Wallet Management ---
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
        CancellationToken cancellationToken = default,
        string? type = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        int? minAmount = null,
        int? maxAmount = null);

    Task<Result<CreditReservationDto>> ReserveCreditsAsync(
        ReserveCreditsRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<CreditTransactionDto>> ConfirmConsumeAsync(
        Guid workspaceId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<Result<bool>> RefundReservationAsync(
        Guid workspaceId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<Result<CreditTransactionDto>> AdjustCreditsAsync(
        Guid subscriptionId,
        int amount,
        string reason,
        string adminUserId,
        CancellationToken cancellationToken = default);

    Task<Result<PagedResult<CreditTransactionDto>>> GetGlobalCreditHistoryAsync(
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default,
        Guid? workspaceId = null,
        string? type = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        int? minAmount = null,
        int? maxAmount = null);
}
