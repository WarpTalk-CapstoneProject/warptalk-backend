using System;
using System.Collections.Generic;

namespace WarpTalk.BillingService.Application.DTOs;

// ============================================================================
// REQUEST DTOs
// ============================================================================

public record RecordUsageRequest(
    Guid HostWorkspaceId,
    Guid UserId,
    string UsageType,
    string Unit,
    decimal Quantity,
    int CreditsConsumed,
    int? DurationSeconds,
    Guid? TranslationRoomId = null,
    Guid? SegmentId = null,
    string? Details = null);

// ============================================================================
// USAGE / ANALYTICS RESPONSE DTOs
// ============================================================================

public record UsageBreakdownDto(
    string UsageType,
    int CreditsConsumed,
    decimal Quantity);

public record BillingReportDto(
    Guid WorkspaceId,
    int Month,
    int Year,
    int StartingBalance,
    int EndingBalance,
    int TotalTopUpCredits,
    int TotalConsumedCredits,
    decimal? AverageTranslationCostPer100Chars,
    int? AverageCostPerMeeting,
    IReadOnlyList<UsageBreakdownDto> UsageBreakdown);

public record FeatureAdoptionDto(
    string UsageType,
    int UsageCount,
    int TotalCreditsConsumed);

public record GlobalBillingMetricsDto(
    int TotalBalance,
    int ActiveWorkspaces,
    int MonthlyUsage,
    int AuditEventsLast30Days);

public record MonthlyUsageDto(
    int Month,
    string MonthName,
    int ConsumedCredits,
    int TopUpCredits);

public record UsageChartDto(
    int Year,
    IReadOnlyList<MonthlyUsageDto> MonthlyData);

public record UsageSummaryDto(
    string UsageType,
    int TotalCreditsConsumed);

public record TopWorkspaceDto(
    Guid WorkspaceId,
    string? WorkspaceName,
    int TotalCreditsConsumed);

