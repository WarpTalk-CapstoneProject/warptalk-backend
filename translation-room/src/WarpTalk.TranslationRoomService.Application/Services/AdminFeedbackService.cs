using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.Shared.Contracts.Admin;
using WarpTalk.TranslationRoomService.Application.DTOs.Admin;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Services;

/// <inheritdoc cref="IAdminFeedbackService"/>
public class AdminFeedbackService : IAdminFeedbackService
{
    /// <summary>
    /// A year. Long enough for "how did the product do this term", short enough that a typo in a
    /// date cannot ask Postgres to aggregate the whole table.
    /// </summary>
    private const int MaxRangeDays = 366;

    /// <summary>Default window when the caller states none. Stated in the response either way.</summary>
    private const int DefaultRangeDays = 30;

    /// <summary>The overall rating's scale, as the feedback write path validates it.</summary>
    private const int MinOverallRating = 1;
    private const int MaxOverallRating = 5;

    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AdminFeedbackService> _logger;

    public AdminFeedbackService(
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        ILogger<AdminFeedbackService> logger)
    {
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<Result<AdminFeedbackSummaryDto>> GetSummaryAsync(
        AdminFeedbackQuery query,
        CancellationToken ct = default)
    {
        if (!TryWindow(query, out var filter, out var error))
        {
            return Result.Failure<AdminFeedbackSummaryDto>(error!, ErrorCodes.ValidationError);
        }

        try
        {
            var repository = _unitOfWork.TranslationRoomFeedbackRepository;
            var (totals, dimensions) = await repository.GetAdminStatsAsync(filter, ct);

            // WT-694: the trend is against the window of the same length immediately before, read
            // with the same query — never extrapolated.
            var previousFilter = filter with
            {
                From = filter.From - (filter.To - filter.From),
                To = filter.From,
            };
            var (previousTotals, previousDimensions) = await repository.GetAdminStatsAsync(previousFilter, ct);

            // Null, not zero. A window in which nothing ended has no response rate — reporting 0%
            // would say every meeting went unrated when none was ever eligible.
            var responseRate = AdminFeedbackInsightsCalculator.ResponseRate(totals);
            var insights = AdminFeedbackInsightsCalculator.Dimensions(
                totals, dimensions, previousTotals, previousDimensions);
            var (confidence, confidenceNote) = AdminFeedbackInsightsCalculator.Survey(totals.ResponseCount, responseRate);
            var (lowest, lowestNote) = AdminFeedbackInsightsCalculator.Lowest(dimensions, insights);

            return Result.Success(new AdminFeedbackSummaryDto(
                filter.From,
                filter.To,
                totals.ResponseCount,
                totals.RoomsWithFeedback,
                totals.EndedRooms,
                responseRate,
                dimensions
                    .Select(d =>
                    {
                        var insight = insights.First(i => i.Dimension == d.Dimension);
                        return new AdminFeedbackDimensionDto(
                            d.Dimension,
                            d.ResponseCount,
                            d.AverageRating,
                            d.Distribution,
                            insight.ResponseShare,
                            insight.Confidence,
                            insight.ConfidenceNote,
                            insight.PreviousResponseCount,
                            insight.PreviousAverageRating,
                            insight.PreviousConfidence,
                            insight.AverageDelta);
                    })
                    .ToList(),
                previousFilter.From,
                previousFilter.To,
                previousTotals.ResponseCount,
                AdminFeedbackInsightsCalculator.ResponseRate(previousTotals),
                confidence,
                confidenceNote,
                lowest,
                lowestNote,
                dimensions.Where(d => d.ResponseCount == 0).Select(d => d.Dimension).ToList(),
                AdminFeedbackInsightsCalculator.MinResponses,
                AdminFeedbackInsightsCalculator.MinRate));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin feedback summary read failed");
            return Result.Failure<AdminFeedbackSummaryDto>(
                "An unexpected error occurred while reading feedback.",
                ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<AdminPagedResult<AdminFeedbackCommentDto>>> GetCommentsAsync(
        AdminFeedbackQuery query,
        CancellationToken ct = default)
    {
        if (!TryWindow(query, out var filter, out var error))
        {
            return Result.Failure<AdminPagedResult<AdminFeedbackCommentDto>>(
                error!, ErrorCodes.ValidationError);
        }

        var sort = string.IsNullOrWhiteSpace(query.Sort) ? "recent" : query.Sort.Trim().ToLowerInvariant();
        if (sort is not ("recent" or "lowest"))
        {
            return Result.Failure<AdminPagedResult<AdminFeedbackCommentDto>>(
                "Unknown sort. Expected one of: recent, lowest.", ErrorCodes.ValidationError);
        }

        if (query.MinRating is < MinOverallRating or > MaxOverallRating
            || query.MaxRating is < MinOverallRating or > MaxOverallRating)
        {
            return Result.Failure<AdminPagedResult<AdminFeedbackCommentDto>>(
                $"minRating and maxRating must be between {MinOverallRating} and {MaxOverallRating}.",
                ErrorCodes.ValidationError);
        }

        if (query.MinRating is { } min && query.MaxRating is { } max && min > max)
        {
            return Result.Failure<AdminPagedResult<AdminFeedbackCommentDto>>(
                "minRating must be less than or equal to maxRating.", ErrorCodes.ValidationError);
        }

        var criteria = new AdminFeedbackCommentCriteria(
            string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim(),
            query.MinRating,
            query.MaxRating);

        var (page, pageSize) = query.Normalize();

        try
        {
            var (rows, total) = await _unitOfWork.TranslationRoomFeedbackRepository
                .GetAdminCommentsAsync(
                    filter, page, pageSize, ct, lowestRatedFirst: sort == "lowest", criteria: criteria);

            return Result.Success(new AdminPagedResult<AdminFeedbackCommentDto>(
                rows
                    .Select(r => new AdminFeedbackCommentDto(
                        r.TranslationRoomId,
                        r.WorkspaceId,
                        r.RoomTitle,
                        r.OverallRating,
                        r.Comment,
                        r.CreatedAt))
                    .ToList(),
                page,
                pageSize,
                total));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin feedback comment read failed");
            return Result.Failure<AdminPagedResult<AdminFeedbackCommentDto>>(
                "An unexpected error occurred while reading feedback comments.",
                ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// Resolves the window, or explains why it cannot. A backwards or oversized range is a caller
    /// mistake; answering it with an empty report would read as "nobody gave feedback", which is
    /// the one thing this screen must never say wrongly.
    /// </summary>
    private bool TryWindow(
        AdminFeedbackQuery query,
        out AdminFeedbackFilter filter,
        out string? error)
    {
        var to = (query.To ?? _timeProvider.GetUtcNow().UtcDateTime).ToUniversalTime();
        var from = (query.From ?? to.AddDays(-DefaultRangeDays)).ToUniversalTime();

        if (from >= to)
        {
            filter = null!;
            error = "'from' must be earlier than 'to'.";
            return false;
        }

        if ((to - from).TotalDays > MaxRangeDays)
        {
            filter = null!;
            error = $"Date range must not exceed {MaxRangeDays} days.";
            return false;
        }

        filter = new AdminFeedbackFilter(from, to, query.WorkspaceId);
        error = null;
        return true;
    }
}
