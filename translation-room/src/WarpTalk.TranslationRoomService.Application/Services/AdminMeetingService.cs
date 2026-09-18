using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.TranslationRoomService.Application.DTOs.Admin;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.TranslationRoomService.Application.Services;

/// <inheritdoc cref="IAdminMeetingService"/>
public class AdminMeetingService : IAdminMeetingService
{
    /// <summary>
    /// Every real status, plus the pseudo-status "live".
    ///
    /// "live" exists because the question an administrator actually asks is "is anyone in a
    /// meeting right now", and the answer spans two statuses — IN_PROGRESS and PAUSED. Making
    /// them type it as two filters would guarantee somebody eventually types one.
    /// </summary>
    private static readonly string[] Statuses =
    [
        "live",
        nameof(RoomStatus.SCHEDULED),
        nameof(RoomStatus.WAITING),
        nameof(RoomStatus.IN_PROGRESS),
        nameof(RoomStatus.PAUSED),
        nameof(RoomStatus.ENDED),
        nameof(RoomStatus.CANCELLED),
        nameof(RoomStatus.EXPIRED),
        nameof(RoomStatus.FAILED),
    ];

    private static readonly string[] Sorts = ["recent_desc", "recent_asc", "duration_desc"];

    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<AdminMeetingService> _logger;

    public AdminMeetingService(IUnitOfWork unitOfWork, ILogger<AdminMeetingService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result<AdminPagedResult<AdminMeetingSummaryDto>>> GetDirectoryAsync(
        AdminMeetingDirectoryQuery query,
        CancellationToken ct = default)
    {
        var status = Normalize(query.Status);
        if (status != null && !Statuses.Contains(status, StringComparer.OrdinalIgnoreCase))
        {
            return Result.Failure<AdminPagedResult<AdminMeetingSummaryDto>>(
                $"Unknown status. Expected one of: {string.Join(", ", Statuses)}.",
                ErrorCodes.ValidationError);
        }

        if (!AdminSort.TryResolve(query.Sort, Sorts, "recent_desc", out var sort))
        {
            return Result.Failure<AdminPagedResult<AdminMeetingSummaryDto>>(
                $"Unknown sort. Expected one of: {string.Join(", ", Sorts)}.",
                ErrorCodes.ValidationError);
        }

        // A backwards window is a caller mistake, not an empty result: returning nothing would
        // read as "no meetings happened", which is the one answer this screen must never give
        // wrongly.
        if (query.From is { } from && query.To is { } to && from > to)
        {
            return Result.Failure<AdminPagedResult<AdminMeetingSummaryDto>>(
                "'from' must not be after 'to'.", ErrorCodes.ValidationError);
        }

        var (page, pageSize) = query.Normalize();

        try
        {
            // "live" is passed through lowercase; every real status is stored upper-case, which is
            // why the filter compares them as they are rather than normalising both.
            var resolvedStatus = status == null
                ? null
                : status.Equals("live", StringComparison.OrdinalIgnoreCase)
                    ? "live"
                    : status.ToUpperInvariant();

            var (rows, total) = await _unitOfWork.TranslationRoomRepository.GetAdminDirectoryAsync(
                new AdminMeetingFilter(resolvedStatus, query.WorkspaceId, query.From, query.To, sort),
                page,
                pageSize,
                ct);

            if (rows.Count == 0)
            {
                return Result.Success(new AdminPagedResult<AdminMeetingSummaryDto>(
                    Array.Empty<AdminMeetingSummaryDto>(), page, pageSize, total));
            }

            // Counted in the database for the whole page, not per row. "Who turned up" rather than
            // "who is here now": a finished meeting has nobody connected, and live occupancy would
            // report every past meeting as empty.
            var attended = await _unitOfWork.TranslationRoomParticipantRepository
                .CountEverJoinedByRoomsAsync(rows.Select(r => r.Id).ToList(), ct);

            return Result.Success(new AdminPagedResult<AdminMeetingSummaryDto>(
                rows.Select(row => ToSummary(row, attended.GetValueOrDefault(row.Id))).ToList(),
                page,
                pageSize,
                total));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin meeting directory read failed. Status: {Status}", status);
            return Result.Failure<AdminPagedResult<AdminMeetingSummaryDto>>(
                "An unexpected error occurred while reading meetings.",
                ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<AdminMeetingCountsDto>> GetCountsAsync(CancellationToken ct = default)
    {
        try
        {
            // Midnight UTC. Deliberately not the caller's local midnight: the figure is a
            // platform-wide one and would otherwise mean a different window per administrator.
            var since = DateTime.UtcNow.Date;
            var (live, startedToday) = await _unitOfWork.TranslationRoomRepository
                .GetAdminCountsAsync(since, ct);

            return Result.Success(new AdminMeetingCountsDto(live, startedToday));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin meeting counts read failed.");
            return Result.Failure<AdminMeetingCountsDto>(
                "An unexpected error occurred while counting meetings.",
                ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<AdminMeetingInsightsDto>> GetInsightsAsync(
        AdminInsightsQuery query,
        CancellationToken ct = default)
    {
        if (!AdminComparisonRange.TryResolve(query, out var window, out var error))
        {
            return Result.Failure<AdminMeetingInsightsDto>(error!, ErrorCodes.ValidationError);
        }

        try
        {
            var now = DateTime.UtcNow;
            var rooms = _unitOfWork.TranslationRoomRepository;

            // One read covering both windows (previousMonth of a long window can overlap the
            // current one), then clipped per window in memory.
            var spanFrom = window.PreviousFrom < window.From ? window.PreviousFrom : window.From;
            var spanTo = window.PreviousTo > window.To ? window.PreviousTo : window.To;
            var spans = await rooms.GetAdminMeetingSpansAsync(spanFrom, spanTo, ct);

            var current = AdminMeetingInsightsCalculator.Totals(spans, window.From, window.To, now);
            var previous = AdminMeetingInsightsCalculator.Totals(spans, window.PreviousFrom, window.PreviousTo, now);
            var byDay = AdminMeetingInsightsCalculator.ByDay(spans, window.From, window.To, now, window.TimeZone);

            // Same definitions as GET /admin/meetings/counts, but "today" is the local day of the
            // request's tz: a Vietnam admin's today began at 17:00Z yesterday.
            var todayStart = AdminComparisonRange.LocalDayOf(now, window.TimeZone).Start;
            var (live, startedToday) = await rooms.GetAdminCountsAsync(todayStart, ct);

            return Result.Success(new AdminMeetingInsightsDto(
                window.Range,
                window.PreviousRange,
                [
                    new AdminInsightMetric(
                        "meetingsHeld", current.MeetingsHeld, previous.MeetingsHeld, AdminInsightUnits.Count, true),
                    new AdminInsightMetric(
                        "hoursTranslated", current.Hours, previous.Hours, AdminInsightUnits.Hours, true,
                        AdminMeetingInsightsCalculator.HoursNote(current, previous)),
                ],
                byDay.Select(d => new AdminMeetingDayDto(d.Date, d.Meetings, d.Hours)).ToList(),
                live,
                startedToday));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin meeting insights read failed.");
            return Result.Failure<AdminMeetingInsightsDto>(
                "An unexpected error occurred while reading meeting insights.",
                ErrorCodes.InternalServerError);
        }
    }

    private AdminMeetingSummaryDto ToSummary(AdminMeetingRow row, int attended)
        => new(
            row.Id,
            row.WorkspaceId,
            row.Title,
            row.TranslationRoomCode,
            row.Status,
            row.TranslationRoomType,
            row.SourceLanguage,
            SplitLanguages(row.TargetLanguages),
            row.ScheduledAt,
            row.StartedAt,
            row.EndedAt,
            row.DurationSeconds,
            attended,
            row.CreatedAt);

    /// <summary>
    /// target_languages is a JSONB array, not a delimited string — and this is the second reader
    /// to learn that the hard way, so it goes through the helper every other reader uses rather
    /// than parsing it here.
    ///
    /// A malformed value yields an empty list instead of throwing. This is a directory: one room
    /// whose column was written by an older producer must not take the whole page down.
    /// </summary>
    private string[] SplitLanguages(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();

        try
        {
            return Helpers.LanguageHelper.ParseTargetLanguages(value).ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unreadable target_languages on an admin meeting row.");
            return Array.Empty<string>();
        }
    }


    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
