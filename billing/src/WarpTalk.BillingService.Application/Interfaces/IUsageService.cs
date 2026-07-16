using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IUsageService
{
    int CalculateCreditCost(int audioSeconds, int tokenCount, int gpuInferenceMs, bool isVoiceClone, Plan plan);
    
    Task<Result<CreditBalanceDto>> RecordUsageAsync(
        RecordUsageRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<bool>> LogUsageOnlyAsync(
        RecordUsageRequest request,
        CancellationToken cancellationToken = default);

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

    // --- Service Rates ---
    Result<ServiceRatesDto> GetServiceRates();
    
    Task<Result<ServiceRatesDto>> UpdateServiceRatesAsync(
        UpdateServiceRatesRequest request,
        CancellationToken cancellationToken = default);
}
