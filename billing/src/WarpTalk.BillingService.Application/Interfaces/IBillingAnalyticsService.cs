using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IBillingAnalyticsService
{
    Task<Result<BillingReportDto>> GetBillingReportAsync(Guid workspaceId, BillingReportQuery query, CancellationToken cancellationToken = default);
    Task<Result<UsageChartDto>> GetWorkspaceUsageChartAsync(Guid workspaceId, UsageChartQuery query, CancellationToken cancellationToken = default);
    Task<Result<IEnumerable<FeatureAdoptionDto>>> GetWorkspaceFeatureAdoptionAsync(Guid workspaceId, UsageChartQuery query, CancellationToken cancellationToken = default);

    Task<Result<GlobalBillingMetricsDto>> GetGlobalMetricsAsync(CancellationToken cancellationToken = default);
    Task<Result<UsageChartDto>> GetGlobalUsageChartAsync(UsageChartQuery query, CancellationToken cancellationToken = default);
    Task<Result<IEnumerable<UsageSummaryDto>>> GetGlobalUsageBreakdownAsync(UsageChartQuery query, CancellationToken cancellationToken = default);
    Task<Result<IEnumerable<TopWorkspaceDto>>> GetTopWorkspacesAsync(UsageChartQuery query, CancellationToken cancellationToken = default);
    Task<Result<IEnumerable<UsageAlertDto>>> GetUsageAlertsAsync(CancellationToken cancellationToken = default);
}
