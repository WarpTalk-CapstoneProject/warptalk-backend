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
using static WarpTalk.BillingService.Application.Services.AdminBillingInsightsCalculator;

namespace WarpTalk.BillingService.Application.Services;

/// <inheritdoc cref="IAdminBillingInsightsService"/>
public sealed class AdminBillingInsightsService : IAdminBillingInsightsService
{
    private const int TopWorkspaceCount = 5;
    private const int RecentPaymentCount = 8;
    private const int EndingSoonCount = 8;
    private const int EndingSoonDays = 14;
    private const int HighUsageAlertCount = 5;

    /// <summary>Same bar as BillingAnalyticsService.GetUsageAlertsAsync, so both alert lists agree.</summary>
    private const long HighUsageCredits24h = 50_000;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IUsageRateCardRepository _pricingConfig;
    private readonly IWorkspaceClient _workspaceClient;
    private readonly ILogger<AdminBillingInsightsService> _logger;
    private readonly TimeProvider _time;

    public AdminBillingInsightsService(
        IUnitOfWork unitOfWork,
        IUsageRateCardRepository pricingConfig,
        IWorkspaceClient workspaceClient,
        ILogger<AdminBillingInsightsService> logger,
        TimeProvider? timeProvider = null)
    {
        _unitOfWork = unitOfWork;
        _pricingConfig = pricingConfig;
        _workspaceClient = workspaceClient;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result<AdminBillingInsightsDto>> GetInsightsAsync(AdminInsightsQuery query, CancellationToken ct = default)
    {
        if (!AdminComparisonRange.TryResolve(query, query.Compare, out var window, out var error))
        {
            return Result.Failure<AdminBillingInsightsDto>(error!, ErrorCodes.ValidationError);
        }

        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var fx = await ReadFxAsync(ct);
            var current = await ReadPeriodAsync(window.Range, now, fx, ct);
            var previous = await ReadPeriodAsync(window.PreviousRange, now, fx, ct);

            var dayBuckets = await _unitOfWork.PaymentRepository.GetCountedPaidTotalsByUtcBucketAsync(
                window.From, window.To, monthly: false, ct);
            var monthWindow = RevenueMonthWindow(window.To);
            var monthBuckets = await _unitOfWork.PaymentRepository.GetCountedPaidTotalsByUtcBucketAsync(
                monthWindow.From, monthWindow.To, monthly: true, ct);

            var byService = await _unitOfWork.CreditTransactionRepository.GetConsumedByChargeTypeAsync(
                window.From, window.To, ct);
            var top = await _unitOfWork.CreditTransactionRepository.GetTopConsumingWorkspacesAsync(
                window.From, window.To, TopWorkspaceCount, cancellationToken: ct);
            var names = await ResolveNamesAsync(top.Select(t => t.WorkspaceId), ct);

            var metrics = new List<AdminInsightMetric>
            {
                Metric("revenue", AdminInsightUnits.Money, true, current.Revenue, previous.Revenue),
                Metric("payments", AdminInsightUnits.Count, true, current.Payments, previous.Payments),
                Metric("failedPayments", AdminInsightUnits.Count, false, current.FailedPayments, previous.FailedPayments),
                Metric("newSubscriptions", AdminInsightUnits.Count, true, current.NewSubscriptions, previous.NewSubscriptions),
                Metric("cancelledSubscriptions", AdminInsightUnits.Count, false, current.CancelledSubscriptions, previous.CancelledSubscriptions),
                Metric("creditsConsumed", AdminInsightUnits.Credits, true, current.CreditsConsumed, previous.CreditsConsumed),
                Metric("overageCredits", AdminInsightUnits.Credits, true, current.OverageCredits, previous.OverageCredits),
                Metric("aiProviderCost", AdminInsightUnits.Money, false, current.AiProviderCost, previous.AiProviderCost),
                Metric("grossMargin", AdminInsightUnits.Money, true, current.GrossMargin, previous.GrossMargin),
                Metric("revenuePerPayment", AdminInsightUnits.Money, true, current.RevenuePerPayment, previous.RevenuePerPayment),
            };

            return Result.Success(new AdminBillingInsightsDto(
                window.Range,
                window.PreviousRange,
                now,
                metrics,
                RevenueByDay(window.From, window.To, dayBuckets, fx),
                RevenueByMonth(window.To, monthBuckets, fx),
                byService.Select(s => new AdminCreditsByServiceDto(s.UsageType, s.Credits)).ToList(),
                top.Select(t => new AdminTopWorkspaceCreditsDto(t.WorkspaceId, NameOf(names, t.WorkspaceId), t.Credits)).ToList()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin billing insights failed. From: {From} To: {To}", window.From, window.To);
            return Result.Failure<AdminBillingInsightsDto>(
                "An unexpected error occurred while building billing insights.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<AdminBillingSnapshotDto>> GetSnapshotAsync(CancellationToken ct = default)
    {
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var fx = await ReadFxAsync(ct);

            // Revenue today / yesterday, on the UTC calendar like every other insights series.
            var today = DateOnly.FromDateTime(now);
            var yesterdayStart = now.Date.AddDays(-1);
            var tomorrowStart = now.Date.AddDays(1);
            var dayBuckets = await _unitOfWork.PaymentRepository.GetCountedPaidTotalsByUtcBucketAsync(
                DateTime.SpecifyKind(yesterdayStart, DateTimeKind.Utc), DateTime.SpecifyKind(tomorrowStart, DateTimeKind.Utc), monthly: false, ct);
            decimal RevenueOn(DateOnly day) => ToVnd(
                    dayBuckets.Where(b => new DateOnly(b.Year, b.Month, b.Day) == day)
                        .Select(b => new MoneyPart(b.Currency, b.Total, 1)),
                    fx)
                .Amount ?? 0m;

            // Subscriptions right now. The same row set and rules as the subscriptions page summary.
            var active = await _unitOfWork.SubscriptionRepository.GetActiveForRevenueAsync(ct);
            var recurring = active.Where(AdminSubscriptionRevenue.IsRecurring).ToList();
            var mrr = ToVnd(
                AdminSubscriptionRevenue.MonthlyRecurring(recurring).Select(m => new MoneyPart(m.Currency, m.Amount, 1)),
                fx);

            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var flows = await _unitOfWork.SubscriptionRepository.GetSubscriptionFlowCountsAsync(monthStart, now, now, ct);

            var outstandingRows = await _unitOfWork.InvoiceRepository.GetOutstandingAsync(ct);
            var outstandingTotal = ToVnd(outstandingRows.Select(i => new MoneyPart(i.Currency, i.Total, 1)), fx);
            var pastDueInvoices = outstandingRows.Where(i => i.DueAt is { } due && due < now).ToList();
            var oldestPastDue = pastDueInvoices.OrderBy(i => i.DueAt).FirstOrDefault();

            var openLeads = await _unitOfWork.SalesInquiryRepository.CountAsync(
                lead => lead.Status == SalesInquiryConstants.Statuses.New
                        || lead.Status == SalesInquiryConstants.Statuses.Reviewing,
                ct);

            var recent = await _unitOfWork.PaymentRepository.GetRecentChargesAsync(RecentPaymentCount, ct);
            var endingSoon = await _unitOfWork.SubscriptionRepository.GetEndingSoonAsync(
                now, now.AddDays(EndingSoonDays), EndingSoonCount, ct);
            var highUsage = await _unitOfWork.CreditTransactionRepository.GetTopConsumingWorkspacesAsync(
                now.AddHours(-24), now, HighUsageAlertCount, HighUsageCredits24h, ct);

            var names = await ResolveNamesAsync(
                recent.Where(p => p.WorkspaceId.HasValue).Select(p => p.WorkspaceId!.Value)
                    .Concat(endingSoon.Select(s => s.WorkspaceId))
                    .Concat(highUsage.Select(h => h.WorkspaceId))
                    .Concat(oldestPastDue is null ? Enumerable.Empty<Guid>() : new[] { oldestPastDue.WorkspaceId }),
                ct);

            return Result.Success(new AdminBillingSnapshotDto(
                now,
                RevenueOn(today),
                RevenueOn(today.AddDays(-1)),
                recurring.Count == 0 ? 0m : mrr.Amount,
                ConversionNote(mrr, fx),
                recurring.Count,
                ActiveByCycle(recurring),
                new AdminChurnRateMonthDto(
                    flows.CancelledSubscriptions,
                    flows.ActiveAtStart,
                    ChurnRate(flows.CancelledSubscriptions, flows.ActiveAtStart)),
                active.Count(row => IsInTrial(row, now)),
                active.Count(row => row.TrialEndsAt is { } end && end > now && end <= now.AddDays(7)),
                active.Count(IsPastDue),
                active.Count(row => row.ServiceState == SubscriptionConstants.ServiceStates.Suspended),
                active.Select(row => row.WorkspaceId).Distinct().Count(),
                active.Sum(row => (long)row.CreditsRemaining),
                new AdminOutstandingInvoicesDto(
                    outstandingRows.Count,
                    outstandingRows.Count == 0 ? 0m : outstandingTotal.Amount,
                    pastDueInvoices.Count,
                    oldestPastDue?.DueAt is { } oldestDue ? (int)Math.Floor((now - oldestDue).TotalDays) : null,
                    oldestPastDue is null ? null : NameOf(names, oldestPastDue.WorkspaceId)),
                openLeads,
                SubscriptionsByPlan(active, now),
                recent.Select(p => new AdminRecentPaymentDto(
                        p.WorkspaceId,
                        p.WorkspaceId is { } id ? NameOf(names, id) : null,
                        p.TotalAmount,
                        p.Currency.ToUpperInvariant(),
                        p.Status,
                        PaymentMethodLabel(p.Provider, p.PaymentMethod),
                        p.At))
                    .ToList(),
                endingSoon.Select(s => new AdminEndingSoonDto(
                        s.WorkspaceId,
                        NameOf(names, s.WorkspaceId),
                        s.PlanName,
                        s.EndsAt,
                        !s.AutoRenew || s.Status == SubscriptionConstants.SubscriptionStatuses.Cancelled))
                    .ToList(),
                highUsage.Select(h => new AdminHighUsageAlertDto(h.WorkspaceId, NameOf(names, h.WorkspaceId), h.Credits)).ToList()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin billing snapshot failed.");
            return Result.Failure<AdminBillingSnapshotDto>(
                "An unexpected error occurred while building the billing snapshot.", ErrorCodes.InternalServerError);
        }
    }

    private async Task<PeriodMetrics> ReadPeriodAsync(
        AdminInsightRange range, DateTime now, decimal? fx, CancellationToken ct)
    {
        var paid = await _unitOfWork.PaymentRepository.GetCountedPaidTotalsAsync(range.From, range.To, ct);
        var duplicates = await _unitOfWork.PaymentRepository.CountStripeInvoiceDuplicatesAsync(range.From, range.To, ct);
        var failed = await _unitOfWork.PaymentRepository.CountFailedAsync(range.From, range.To, ct);
        var consumption = await _unitOfWork.CreditTransactionRepository.GetConsumptionTotalsAsync(range.From, range.To, ct);
        var flows = await _unitOfWork.SubscriptionRepository.GetSubscriptionFlowCountsAsync(range.From, range.To, now, ct);

        var (revenue, revenueTotal) = Revenue(paid, duplicates, fx);
        var aiCost = AiProviderCost(consumption, fx);

        return new PeriodMetrics(
            revenue,
            AdminBillingInsightsCalculator.Payments(paid),
            Count(failed),
            NewSubscriptions(flows),
            Count(flows.CancelledSubscriptions, CancelledSubscriptionsNote),
            new MetricSide(consumption.CreditsConsumed, null),
            new MetricSide(consumption.OverageCredits, OverageNote),
            aiCost,
            GrossMargin(revenue, aiCost, consumption),
            RevenuePerPayment(revenue, revenueTotal));
    }

    private async Task<decimal?> ReadFxAsync(CancellationToken ct)
    {
        var fx = await _pricingConfig.ReadPricingConfigValueAsync(FxRateConfigKey, 0m, ct);
        return fx > 0 ? fx : null;
    }

    private async Task<IReadOnlyDictionary<Guid, string>> ResolveNamesAsync(IEnumerable<Guid> workspaceIds, CancellationToken ct)
    {
        var ids = workspaceIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, string>();

        try
        {
            var result = await _workspaceClient.GetWorkspaceNamesAsync(ids, ct);
            if (result.IsSuccess && result.Value is not null) return result.Value;
            _logger.LogWarning("Admin billing insights could not resolve workspace names: {Error}", result.Error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Admin billing insights could not resolve workspace names.");
        }

        return new Dictionary<Guid, string>();
    }

    private static string? NameOf(IReadOnlyDictionary<Guid, string> names, Guid workspaceId)
        => names.TryGetValue(workspaceId, out var name) && !string.IsNullOrWhiteSpace(name) ? name : null;

    private sealed record PeriodMetrics(
        MetricSide Revenue,
        MetricSide Payments,
        MetricSide FailedPayments,
        MetricSide NewSubscriptions,
        MetricSide CancelledSubscriptions,
        MetricSide CreditsConsumed,
        MetricSide OverageCredits,
        MetricSide AiProviderCost,
        MetricSide GrossMargin,
        MetricSide RevenuePerPayment);
}
