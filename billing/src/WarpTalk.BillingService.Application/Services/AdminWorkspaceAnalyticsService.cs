using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.BillingService.Application.Services;

public class AdminWorkspaceAnalyticsService : IAdminWorkspaceAnalyticsService
{
    private const int MaxRangeDays = 366;

    private static readonly string[] AllowedTransactionTypes =
    [
        "consume", "topup", "refund", "reserve", "adjustment",
    ];

    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<AdminWorkspaceAnalyticsService> _logger;

    public AdminWorkspaceAnalyticsService(
        IUnitOfWork unitOfWork,
        ILogger<AdminWorkspaceAnalyticsService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result<AdminWorkspaceAnalyticsDto>> GetAnalyticsAsync(
        Guid workspaceId,
        AdminDateRange range,
        CancellationToken ct = default)
    {
        if (!range.TryNormalize(MaxRangeDays, out var from, out var to, out var error))
        {
            return Result.Failure<AdminWorkspaceAnalyticsDto>(error!, ErrorCodes.ValidationError);
        }

        try
        {
            var credits = await BuildCreditSummaryAsync(workspaceId, ct);

            // One materialization of the window's usage rows feeds the total, the series, and
            // the breakdown, so the three cannot disagree — that is the reconciliation
            // requirement, enforced by construction rather than by a later assertion.
            var _usageList = await _unitOfWork.UsageRecordRepository.FindAsync(record => record.WorkspaceId == workspaceId
                                 && record.RecordedAt >= from
                                 && record.RecordedAt < to, ct);
            var usage = _usageList.Select(record => new UsageProjection(
                    record.RecordedAt,
                    record.UsageType,
                    record.CreditsConsumed,
                    record.Quantity,
                    record.TranslationRoomId,
                    record.UserId)).ToList();

            var _toppedUpList = await _unitOfWork.CreditTransactionRepository.FindAsync(tx => tx.WorkspaceId == workspaceId
                             && tx.CreatedAt >= from
                             && tx.CreatedAt < to
                             && tx.Amount > 0, ct);
            var toppedUp = _toppedUpList.Sum(tx => (int?)tx.Amount) ?? 0;

            var series = usage
                .GroupBy(record => record.RecordedAt.Date)
                .OrderBy(group => group.Key)
                .Select(group => new AdminWorkspaceUsagePointDto(
                    DateTime.SpecifyKind(group.Key, DateTimeKind.Utc),
                    group.Sum(record => record.CreditsConsumed),
                    group.Count()))
                .ToList();

            var breakdown = usage
                .GroupBy(record => record.UsageType)
                .OrderByDescending(group => group.Sum(record => record.CreditsConsumed))
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new AdminWorkspaceFeatureUsageDto(
                    group.Key,
                    group.Sum(record => record.CreditsConsumed),
                    group.Sum(record => record.Quantity),
                    group.Count()))
                .ToList();

            return Result.Success(new AdminWorkspaceAnalyticsDto(
                workspaceId,
                from,
                to,
                credits,
                usage.Sum(record => record.CreditsConsumed),
                toppedUp,
                usage.Where(record => record.TranslationRoomId != null)
                    .Select(record => record.TranslationRoomId!.Value)
                    .Distinct()
                    .Count(),
                usage.Select(record => record.UserId).Distinct().Count(),
                series,
                breakdown));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin workspace analytics failed. WorkspaceId: {WorkspaceId}", workspaceId);
            return Result.Failure<AdminWorkspaceAnalyticsDto>(
                "An unexpected error occurred while building workspace analytics.",
                ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<AdminPagedResult<AdminCreditTransactionDto>>> GetCreditTransactionsAsync(
        Guid workspaceId,
        AdminCreditTransactionQuery query,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(query.Type)
            && !AllowedTransactionTypes.Contains(query.Type.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return Result.Failure<AdminPagedResult<AdminCreditTransactionDto>>(
                $"Unknown transaction type. Expected one of: {string.Join(", ", AllowedTransactionTypes)}.",
                ErrorCodes.ValidationError);
        }

        if (query.From is { } from && query.To is { } to && from >= to)
        {
            return Result.Failure<AdminPagedResult<AdminCreditTransactionDto>>(
                "'from' must be earlier than 'to'.", ErrorCodes.ValidationError);
        }

        if (query.MinAmount is { } min && query.MaxAmount is { } max && min > max)
        {
            return Result.Failure<AdminPagedResult<AdminCreditTransactionDto>>(
                "minAmount must be less than or equal to maxAmount.", ErrorCodes.ValidationError);
        }

        var (page, pageSize) = query.Normalize();

        try
        {
            System.Linq.Expressions.Expression<Func<WarpTalk.BillingService.Domain.Entities.CreditTransaction, bool>> predicate = tx =>
                tx.WorkspaceId == workspaceId &&
                (string.IsNullOrWhiteSpace(query.Type) || tx.Type.ToLower() == query.Type.Trim().ToLowerInvariant()) &&
                (!query.From.HasValue || tx.CreatedAt >= query.From.Value.ToUniversalTime()) &&
                (!query.To.HasValue || tx.CreatedAt < query.To.Value.ToUniversalTime()) &&
                (!query.ReferenceId.HasValue || tx.ReferenceId == query.ReferenceId.Value) &&
                (!query.MinAmount.HasValue || tx.Amount >= query.MinAmount.Value) &&
                (!query.MaxAmount.HasValue || tx.Amount <= query.MaxAmount.Value);

            var total = await _unitOfWork.CreditTransactionRepository.CountAsync(predicate, ct);
            var pagedItems = await _unitOfWork.CreditTransactionRepository.GetPagedAsync(
                predicate,
                (page - 1) * pageSize,
                pageSize,
                q => q.OrderByDescending(tx => tx.CreatedAt).ThenByDescending(tx => tx.Id),
                ct);

            var items = pagedItems.Select(tx => new AdminCreditTransactionDto(
                tx.Id,
                tx.CreatedAt,
                tx.Type,
                tx.Description,
                tx.ReferenceId,
                tx.ReferenceType,
                tx.Amount,
                tx.BalanceAfter,
                tx.Currency,
                tx.Status)).ToList();

            return Result.Success(
                new AdminPagedResult<AdminCreditTransactionDto>(items, page, pageSize, total));
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Admin credit transaction query failed. WorkspaceId: {WorkspaceId}", workspaceId);
            return Result.Failure<AdminPagedResult<AdminCreditTransactionDto>>(
                "An unexpected error occurred while querying credit transactions.",
                ErrorCodes.InternalServerError);
        }
    }

    private async Task<AdminWorkspaceCreditSummaryDto> BuildCreditSummaryAsync(
        Guid workspaceId,
        CancellationToken ct)
    {
        var subs = await _unitOfWork.SubscriptionRepository.FindAsync(
            sub => sub.WorkspaceId == workspaceId && sub.DeletedAt == null,
            ct);
        var subscription = subs.OrderByDescending(sub => sub.IsActive)
                               .ThenByDescending(sub => sub.CurrentPeriodEnd)
                               .FirstOrDefault();

        return subscription is null
            ? new AdminWorkspaceCreditSummaryDto(false, null, null, null, null, null)
            : new AdminWorkspaceCreditSummaryDto(
                true,
                subscription.CreditsRemaining,
                subscription.CreditsUsedThisCycle,
                subscription.CurrentPeriodStart,
                subscription.CurrentPeriodEnd,
                subscription.PlanId);
    }

    private sealed record UsageProjection(
        DateTime RecordedAt,
        string UsageType,
        int CreditsConsumed,
        decimal Quantity,
        Guid? TranslationRoomId,
        Guid? UserId);
}
