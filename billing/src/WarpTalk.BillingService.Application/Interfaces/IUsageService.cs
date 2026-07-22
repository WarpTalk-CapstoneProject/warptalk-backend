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

    Task<Result<CreditBalanceDto>> RecordUsageAsync(RecordUsageRequest request, CancellationToken cancellationToken = default);
    Task<Result<bool>> LogUsageOnlyAsync(RecordUsageRequest request, CancellationToken cancellationToken = default);

    Task<Result<BillingReportDto>> GetBillingReportAsync(Guid workspaceId, BillingReportQuery query, CancellationToken cancellationToken = default);
    Task<Result<UsageChartDto>> GetWorkspaceUsageChartAsync(Guid workspaceId, UsageChartQuery query, CancellationToken cancellationToken = default);
    Task<Result<IEnumerable<FeatureAdoptionDto>>> GetWorkspaceFeatureAdoptionAsync(Guid workspaceId, UsageChartQuery query, CancellationToken cancellationToken = default);

    Task<Result<GlobalBillingMetricsDto>> GetGlobalMetricsAsync(CancellationToken cancellationToken = default);
    Task<Result<UsageChartDto>> GetGlobalUsageChartAsync(UsageChartQuery query, CancellationToken cancellationToken = default);
    Task<Result<IEnumerable<UsageSummaryDto>>> GetGlobalUsageBreakdownAsync(UsageChartQuery query, CancellationToken cancellationToken = default);
    Task<Result<IEnumerable<TopWorkspaceDto>>> GetTopWorkspacesAsync(UsageChartQuery query, CancellationToken cancellationToken = default);
    Task<Result<IEnumerable<UsageAlertDto>>> GetUsageAlertsAsync(CancellationToken cancellationToken = default);

    Result<ServiceRatesDto> GetServiceRates();
    Task<Result<ServiceRatesDto>> UpdateServiceRatesAsync(UpdateServiceRatesRequest request, CancellationToken cancellationToken = default);
}
