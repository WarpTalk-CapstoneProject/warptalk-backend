using System;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.TranslationRoomService.Application.DTOs.Admin;

/// <summary>Query string contract for the platform meeting directory. Bound with [FromQuery].</summary>
public record AdminMeetingDirectoryQuery : AdminPageRequest
{
    /// <summary>A room status, or "live" for anything carrying audio right now. Null lists all.</summary>
    public string? Status { get; init; }

    public Guid? WorkspaceId { get; init; }

    /// <summary>Measured against when the meeting happened, not when its row was created.</summary>
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }

    /// <summary>recent_desc | recent_asc | duration_desc. Defaults to recent_desc.</summary>
    public string? Sort { get; init; }
}

/// <summary>
/// One meeting, as much of it as an administrator is entitled to see.
///
/// Metadata only: who held it, how long, in which languages, how it ended. There is no
/// description, no settings and no transcript — what was said belongs to the workspace that held
/// the meeting. The title is the one piece of customer text kept, because a row without it cannot
/// be identified.
/// </summary>
public record AdminMeetingSummaryDto(
    Guid Id,
    Guid WorkspaceId,
    string Title,
    string TranslationRoomCode,
    string Status,
    string TranslationRoomType,
    string SourceLanguage,
    string[] TargetLanguages,
    DateTime? ScheduledAt,
    DateTime? StartedAt,
    DateTime? EndedAt,
    int? DurationSeconds,
    /// <summary>Distinct people who were ever in the room, not who is in it now.</summary>
    int AttendedCount,
    DateTime CreatedAt);

/// <summary>The header counts. Both are read at the same instant so they cannot disagree.</summary>
public record AdminMeetingCountsDto(int LiveNow, int StartedToday);

/// <summary>One UTC day of the meetings chart. <paramref name="Date"/> is <c>yyyy-MM-dd</c>.</summary>
public record AdminMeetingDayDto(string Date, int Meetings, decimal Hours);

/// <summary>
/// <c>GET /api/v1/admin/meetings/insights</c>: metrics <c>meetingsHeld</c> and
/// <c>hoursTranslated</c> over the window, the per-day series for the current window, and the
/// point-in-time <paramref name="LiveNow"/> / <paramref name="StartedToday"/> (UTC day) counts.
/// Definitions live on <c>AdminMeetingInsightsCalculator</c>.
/// </summary>
public record AdminMeetingInsightsDto(
    AdminInsightRange Range,
    AdminInsightRange PreviousRange,
    System.Collections.Generic.IReadOnlyList<AdminInsightMetric> Metrics,
    System.Collections.Generic.IReadOnlyList<AdminMeetingDayDto> MeetingsByDay,
    int LiveNow,
    int StartedToday);

