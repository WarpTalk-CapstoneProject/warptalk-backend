using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.BillingService.Application.Services;

/// <inheritdoc cref="IAdminSubscriptionService"/>
public class AdminSubscriptionService : IAdminSubscriptionService
{
    private static readonly string[] Statuses =
    [
        SubscriptionConstants.SubscriptionStatuses.Pending,
        SubscriptionConstants.SubscriptionStatuses.Active,
        SubscriptionConstants.SubscriptionStatuses.Cancelled,
        SubscriptionConstants.SubscriptionStatuses.Expired,
        SubscriptionConstants.SubscriptionStatuses.Suspended,
    ];

    private static readonly string[] Sorts =
        ["period_end_asc", "period_end_desc", "created_desc", "created_asc", "credits_asc"];

    /// <summary>
    /// The renewal horizon the summary reports. Fourteen days rather than thirty: a month is long
    /// enough that every subscription is nearly always inside it, which makes the number a
    /// constant rather than a warning.
    /// </summary>
    private const int EndingSoonDays = 14;

    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<AdminSubscriptionService> _logger;

    public AdminSubscriptionService(IUnitOfWork unitOfWork, ILogger<AdminSubscriptionService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result<AdminPagedResult<AdminSubscriptionSummaryDto>>> GetDirectoryAsync(
        AdminSubscriptionDirectoryQuery query,
        CancellationToken ct = default)
    {
        var status = Normalize(query.Status);
        if (status != null && !Statuses.Contains(status, StringComparer.Ordinal))
        {
            return Result.Failure<AdminPagedResult<AdminSubscriptionSummaryDto>>(
                $"Unknown status. Expected one of: {string.Join(", ", Statuses)}.",
                ErrorCodes.ValidationError);
        }

        if (!AdminSort.TryResolve(query.Sort, Sorts, "period_end_asc", out var sort))
        {
            return Result.Failure<AdminPagedResult<AdminSubscriptionSummaryDto>>(
                $"Unknown sort. Expected one of: {string.Join(", ", Sorts)}.",
                ErrorCodes.ValidationError);
        }

        var (page, pageSize) = query.Normalize();

        try
        {
            var (rows, total) = await _unitOfWork.SubscriptionRepository.GetAdminDirectoryAsync(
                new AdminSubscriptionFilter(status, Normalize(query.PlanSlug), sort),
                page,
                pageSize,
                ct);

            return Result.Success(new AdminPagedResult<AdminSubscriptionSummaryDto>(
                rows.Select(ToSummary).ToList(), page, pageSize, total));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin subscription directory read failed. Status: {Status}", status);
            return Result.Failure<AdminPagedResult<AdminSubscriptionSummaryDto>>(
                "An unexpected error occurred while reading subscriptions.",
                ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<AdminSubscriptionSummaryTotalsDto>> GetSummaryAsync(
        CancellationToken ct = default)
    {
        try
        {
            // Every ACTIVE row, unpaged. The active set is small — one subscription per paying
            // workspace — and recurring revenue is meaningless computed over a page of twenty.
            var active = await _unitOfWork.SubscriptionRepository.GetActiveForRevenueAsync(ct);

            var now = DateTime.UtcNow;
            var trials = active.Count(row => row.TrialEndsAt != null && row.TrialEndsAt > now);

            // Counted from the SERVICE STATE, not the status: a subscription suspended for an
            // overdue invoice is still status=active, and reading only the status would report
            // every one of them as healthy.
            var pastDue = active.Count(row =>
                row.ServiceState == SubscriptionConstants.ServiceStates.Suspended
                && row.SuspendedReason == SubscriptionConstants.SuspendedReasons.InvoiceOverdue);

            var cancelled = await CountByStatusAsync(
                SubscriptionConstants.SubscriptionStatuses.Cancelled, ct);

            return Result.Success(new AdminSubscriptionSummaryTotalsDto(
                AdminSubscriptionRevenue.MonthlyRecurring(active),
                ActiveCount: active.Count,
                TrialCount: trials,
                PastDueCount: pastDue,
                CancelledCount: cancelled,
                EndingWithin14Days: AdminSubscriptionRevenue.EndingWithin(active, EndingSoonDays, now)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin subscription summary read failed.");
            return Result.Failure<AdminSubscriptionSummaryTotalsDto>(
                "An unexpected error occurred while summarising subscriptions.",
                ErrorCodes.InternalServerError);
        }
    }

    private async Task<int> CountByStatusAsync(string status, CancellationToken ct)
    {
        var (_, total) = await _unitOfWork.SubscriptionRepository.GetAdminDirectoryAsync(
            new AdminSubscriptionFilter(status), page: 1, pageSize: 1, ct);
        return total;
    }

    private static AdminSubscriptionSummaryDto ToSummary(AdminSubscriptionRow row)
        => new(
            row.Id,
            row.WorkspaceId,
            row.Status,
            row.ServiceState,
            row.SuspendedReason,
            row.PlanName,
            row.PlanSlug,
            row.PlanTier,
            row.BillingCycle,
            // Null, not zero, when the subscription is not recurring. A trial that will be worth
            // 1,900,000 VND next week is not worth 0 today — it is not yet an answer.
            AdminSubscriptionRevenue.IsRecurring(row)
                ? AdminMoney.Of(
                    AdminSubscriptionRevenue.MonthlyAmount(row).Amount,
                    AdminSubscriptionRevenue.MonthlyAmount(row).Currency)
                : null,
            row.CreditsRemaining,
            row.CreditsUsedThisCycle,
            row.CurrentPeriodStart,
            row.CurrentPeriodEnd,
            row.AutoRenew,
            row.TrialEndsAt,
            row.CancelledAt,
            row.CreatedAt);

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
