using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface ICreditAndUsageService
{
    // --- Cost Calculation ---
    int CalculateCreditCost(int audioSeconds, int tokenCount, int gpuInferenceMs, bool isVoiceClone, Plan plan);

    // --- Session Heartbeat ---
    Task<Result<Guid>> StartSessionAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<Result<bool>> ProcessHeartbeatAsync(Guid sessionId, Guid workspaceId, CancellationToken cancellationToken = default);

    // --- Credit & Usage Management ---
    Task<Result<CreditBalanceDto>> GetWorkspaceCreditsAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default);

    Task<Result<CreditTransactionDto>> ConsumeCreditsAsync(
        Guid workspaceId,
        ConsumeCreditsRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<CreditBalanceDto>> RecordUsageAsync(
        RecordUsageRequest request,
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

    Task<Result<BillingReportDto>> GetBillingReportAsync(
        Guid workspaceId,
        int year,
        int month,
        CancellationToken cancellationToken = default);

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

    Task TakeSnapshotAsync(Guid subscriptionId, CancellationToken cancellationToken = default);

    Task<Result<CreditTransactionDto>> AdjustCreditsAsync(
        Guid subscriptionId,
        int amount,
        string reason,
        string adminUserId,
        CancellationToken cancellationToken = default);
}
