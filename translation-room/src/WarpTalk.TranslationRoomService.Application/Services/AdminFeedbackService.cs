using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.Shared.Contracts.Admin;
using WarpTalk.TranslationRoomService.Application.DTOs.Admin;
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
            var (totals, dimensions) = await _unitOfWork.TranslationRoomFeedbackRepository
                .GetAdminStatsAsync(filter, ct);

            return Result.Success(new AdminFeedbackSummaryDto(
                filter.From,
                filter.To,
                totals.ResponseCount,
                totals.RoomsWithFeedback,
                totals.EndedRooms,
                // Null, not zero. A window in which nothing ended has no response rate — reporting
                // 0% would say every meeting went unrated when none was ever eligible.
                totals.EndedRooms == 0
                    ? null
                    : (double)totals.RoomsWithFeedback / totals.EndedRooms,
                dimensions
                    .Select(d => new AdminFeedbackDimensionDto(
                        d.Dimension,
                        d.ResponseCount,
                        d.AverageRating,
                        d.Distribution))
                    .ToList()));
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

        var (page, pageSize) = query.Normalize();

        try
        {
            var (rows, total) = await _unitOfWork.TranslationRoomFeedbackRepository
                .GetAdminCommentsAsync(filter, page, pageSize, ct);

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
