using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WarpTalk.Shared.Contracts.Admin;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// The maths behind <c>GET /api/v1/admin/meetings/insights</c>. Pure — the clock is a parameter —
/// so every definition below is pinned by unit tests rather than by whatever day the suite runs.
///
/// <para><b>meetingsHeld</b>: meetings whose <c>started_at</c> falls in the window. A meeting that
/// was only scheduled, or opened but never started, has no <c>started_at</c> and is not held.</para>
///
/// <para><b>hoursTranslated</b>: meeting wall-clock time (<c>started_at</c> → <c>ended_at</c>)
/// that fell INSIDE the window. A meeting spanning a boundary is clipped, so the per-day hours
/// add up to the total and a long meeting across midnight is split between its two days. This is
/// meeting-open time, pauses included — NOT translation-active time: translation_room_sessions
/// does record Start/Stop Translation, but the workspace-deleted cancellation path never closes
/// its session, so summing sessions would count those as running forever.</para>
///
/// <para>Where the end is unknown:</para>
/// <list type="bullet">
/// <item>A live meeting (IN_PROGRESS/PAUSED) runs to <c>now</c>, capped at
/// <see cref="MaxOpenMeetingDuration"/> after it started. The abandoned-room sweep only looks
/// back a bounded distance, so a room can stay "live" for weeks; uncapped, one such row would add
/// 24 hours to every day. Capped meetings are reported in the note.</item>
/// <item>A non-live meeting with no <c>ended_at</c> uses <c>duration_seconds</c> if present, and
/// is otherwise left out of the hours (still counted as held) and reported in the note.</item>
/// </list>
/// </summary>
public static class AdminMeetingInsightsCalculator
{
    public static readonly TimeSpan MaxOpenMeetingDuration = TimeSpan.FromHours(24);

    private static readonly HashSet<string> LiveStatuses = new(StringComparer.Ordinal)
    {
        nameof(RoomStatus.IN_PROGRESS),
        nameof(RoomStatus.PAUSED),
    };

    public sealed record WindowTotals(int MeetingsHeld, decimal Hours, int ExcludedNoEnd, int CappedLive);

    public sealed record DayTotals(string Date, int Meetings, decimal Hours);

    public static WindowTotals Totals(IReadOnlyList<AdminMeetingSpan> spans, DateTime from, DateTime to, DateTime now)
    {
        var held = 0;
        var seconds = 0d;
        var excluded = 0;
        var capped = 0;

        foreach (var span in spans)
        {
            var startedInWindow = span.StartedAt >= from && span.StartedAt < to;
            if (startedInWindow) held++;

            var (end, wasCapped) = ResolveEnd(span, now);
            if (end is null)
            {
                if (startedInWindow) excluded++;
                continue;
            }

            var overlap = Overlap(span.StartedAt, end.Value, from, to);
            if (overlap <= 0) continue;

            seconds += overlap;
            if (wasCapped) capped++;
        }

        return new WindowTotals(held, ToHours(seconds), excluded, capped);
    }

    /// <summary>
    /// Every local day of <paramref name="timeZone"/> that <c>[from, to)</c> touches, zero-filled;
    /// partial first/last days are clipped to the window. A meeting belongs to the local day it
    /// started on, and its hours are split at LOCAL midnight — for Vietnam, 17:00Z.
    /// </summary>
    public static IReadOnlyList<DayTotals> ByDay(
        IReadOnlyList<AdminMeetingSpan> spans, DateTime from, DateTime to, DateTime now, TimeZoneInfo timeZone)
    {
        var days = AdminComparisonRange.DaysOf(from, to, timeZone);
        var meetings = new int[days.Count];
        var seconds = new double[days.Count];

        foreach (var span in spans)
        {
            if (span.StartedAt >= from && span.StartedAt < to)
            {
                var startedOn = AdminComparisonRange.IndexOfDay(days, span.StartedAt);
                if (startedOn >= 0) meetings[startedOn]++;
            }

            var (end, _) = ResolveEnd(span, now);
            if (end is null) continue;

            // Only the days this meeting touches, not every day of the window.
            var clippedStart = span.StartedAt > from ? span.StartedAt : from;
            var clippedEnd = end.Value < to ? end.Value : to;
            if (clippedEnd <= clippedStart) continue;

            for (var index = AdminComparisonRange.IndexOfDay(days, clippedStart);
                 index >= 0 && index < days.Count && days[index].Start < clippedEnd;
                 index++)
            {
                seconds[index] += Overlap(clippedStart, clippedEnd, days[index].Start, days[index].End);
            }
        }

        return days
            .Select((day, i) => new DayTotals(day.Key, meetings[i], ToHours(seconds[i])))
            .ToList();
    }

    /// <summary>Null when the note would say nothing.</summary>
    public static string? HoursNote(WindowTotals current, WindowTotals previous)
    {
        var parts = new List<string>();

        if (current.ExcludedNoEnd > 0 || previous.ExcludedNoEnd > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"Excludes meetings with no recorded end time ({current.ExcludedNoEnd} this period, {previous.ExcludedNoEnd} previous)."));
        }

        if (current.CappedLive > 0 || previous.CappedLive > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"Meetings still marked live after {MaxOpenMeetingDuration.TotalHours:0} h are counted as {MaxOpenMeetingDuration.TotalHours:0} h ({current.CappedLive} this period, {previous.CappedLive} previous)."));
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static (DateTime? End, bool Capped) ResolveEnd(AdminMeetingSpan span, DateTime now)
        => ResolveEnd(span.StartedAt, span.EndedAt, span.DurationSeconds, span.Status, now);

    /// <summary>
    /// Where a meeting ended for the purpose of counting its time — the rule documented on this
    /// class, shared with the LiveKit usage of the admin Providers page (MediaUsageCalculator).
    /// </summary>
    public static (DateTime? End, bool Capped) ResolveEnd(
        DateTime startedAt, DateTime? endedAt, int? durationSeconds, string status, DateTime now)
    {
        if (endedAt is { } ended) return (ended, false);

        if (LiveStatuses.Contains(status))
        {
            var cap = startedAt + MaxOpenMeetingDuration;
            return now > cap ? (cap, true) : (now, false);
        }

        if (durationSeconds is { } duration)
        {
            return (startedAt.AddSeconds(duration), false);
        }

        return (null, false);
    }

    private static double Overlap(DateTime start, DateTime end, DateTime from, DateTime to)
    {
        var s = start > from ? start : from;
        var e = end < to ? end : to;
        return e > s ? (e - s).TotalSeconds : 0;
    }

    private static decimal ToHours(double seconds) =>
        Math.Round((decimal)seconds / 3600m, 2, MidpointRounding.AwayFromZero);
}
