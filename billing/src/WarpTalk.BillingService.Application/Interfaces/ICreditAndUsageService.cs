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

    Task<Result<UsageChartDto>> GetWorkspaceUsageChartAsync(
        Guid workspaceId,
        int year,
        CancellationToken cancellationToken = default);

    Task<Result<IEnumerable<FeatureAdoptionDto>>> GetWorkspaceFeatureAdoptionAsync(
        Guid workspaceId,
        int days,
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

    Task<Result<GlobalBillingMetricsDto>> GetGlobalMetricsAsync(
        CancellationToken cancellationToken = default);

    Task<Result<UsageChartDto>> GetGlobalUsageChartAsync(
        int year,
        CancellationToken cancellationToken = default);

    Task<Result<IEnumerable<UsageSummaryDto>>> GetGlobalUsageBreakdownAsync(
        int days,
        CancellationToken cancellationToken = default);

    Task<Result<IEnumerable<TopWorkspaceDto>>> GetTopWorkspacesAsync(
        int days = 30,
        int limit = 5,
        CancellationToken cancellationToken = default);

    Task<Result<IEnumerable<UsageAlertDto>>> GetUsageAlertsAsync(
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

    // --- Service Rates ---
    Result<ServiceRatesDto> GetServiceRates();
    Task<Result<ServiceRatesDto>> UpdateServiceRatesAsync(
        UpdateServiceRatesRequest request,
        CancellationToken cancellationToken = default);
}
