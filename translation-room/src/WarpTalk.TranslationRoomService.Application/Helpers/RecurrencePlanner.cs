using System;
using System.Collections.Generic;
using System.Globalization;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Constants;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>WT-327: a validated, defaulted, guaranteed-to-terminate recurrence rule.</summary>
/// <param name="ByWeekdays">
/// WEEKLY only, ISO weekdays (Monday 1 … Sunday 7), ascending and de-duplicated. Never null for a
/// weekly plan and always null otherwise — the planner resolves the default here so that no
/// downstream caller has to decide what an absent weekday list means.
/// </param>
/// <param name="ByMonthDay">MONTHLY only, 1–31. Never null for a monthly plan, always null otherwise.</param>
public sealed record RecurrencePlan(
    string Type,
    int Interval,
    TimeOnly StartTimeLocal,
    TimeZoneInfo TimeZone,
    DateOnly StartDate,
    DateOnly EndDate,
    IReadOnlyList<int>? ByWeekdays = null,
    int? ByMonthDay = null);

/// <summary>
/// WT-327: turns what a client asked for into a rule the materialiser can trust.
///
/// Separate from the service, and free of the database and the ambient clock, because every
/// interesting failure here is a date-arithmetic edge (booking a daily 8am at 8:01am; an end
/// date in the year 9999; a zone this host has never heard of) and none of them should need a
/// Postgres container to reproduce.
///
/// Every default applied here is a boundary decision, not a convenience:
///  - An omitted start date means "the next occurrence": today if its time has not yet passed in
///    the series' own zone, otherwise tomorrow. Booking a daily 8am at 9am must not silently
///    create a room for 8am this morning.
///  - An omitted end date means <see cref="RecurrenceLimits.DefaultDurationDays"/>, NOT
///    "forever". An indefinite series generates rooms for an abandoned demo workspace until
///    somebody notices the row count.
///  - An explicit end date is bounded by <see cref="RecurrenceLimits.MaxDurationDays"/>, so
///    "forever" cannot be smuggled in as a far-future date.
///
/// InvariantCulture on every parse. The ambient locale is exactly how 0.006575 once became 6575
/// in the billing JSON, and "08:00" is no safer than a decimal.
/// </summary>
public static class RecurrencePlanner
{
    public static Result<RecurrencePlan> Plan(RecurrenceRequest? request, DateTime nowUtc)
    {
        if (request is null)
            return Result.Failure<RecurrencePlan>(RecurrenceMessages.RecurrenceRequired, ErrorCodes.ValidationError);

        var type = RecurrenceTypes.Normalize(request.Type);
        if (type is null)
            return Result.Failure<RecurrencePlan>(RecurrenceMessages.TypeUnrecognised, ErrorCodes.ValidationError);

        if (!RecurrenceTypes.IsSupported(type))
            return Result.Failure<RecurrencePlan>(
                string.Format(CultureInfo.InvariantCulture, RecurrenceMessages.TypeNotSupportedYet, type),
                ErrorCodes.ValidationError);

        if (!TimeOnly.TryParseExact(request.StartTimeLocal?.Trim(), "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var startTime))
        {
            return Result.Failure<RecurrencePlan>(RecurrenceMessages.TimeMalformed, ErrorCodes.ValidationError);
        }

        var timeZone = RecurrenceScheduleCalculator.ResolveTimeZone(request.TimeZone);
        if (timeZone is null)
            return Result.Failure<RecurrencePlan>(RecurrenceMessages.TimeZoneUnknown, ErrorCodes.ValidationError);

        var todayLocal = RecurrenceScheduleCalculator.LocalDateOf(nowUtc, timeZone);

        DateOnly startDate;
        if (string.IsNullOrWhiteSpace(request.StartDateLocal))
        {
            var todayAtTimeUtc = RecurrenceScheduleCalculator.ToUtcInstant(todayLocal, startTime, timeZone);
            startDate = todayAtTimeUtc > nowUtc ? todayLocal : todayLocal.AddDays(1);
        }
        else if (!DateOnly.TryParseExact(request.StartDateLocal.Trim(), "yyyy-MM-dd",
                     CultureInfo.InvariantCulture, DateTimeStyles.None, out startDate))
        {
            return Result.Failure<RecurrencePlan>(RecurrenceMessages.StartDateMalformed, ErrorCodes.ValidationError);
        }

        if (startDate < todayLocal)
            return Result.Failure<RecurrencePlan>(RecurrenceMessages.StartDateInPast, ErrorCodes.ValidationError);

        DateOnly endDate;
        if (string.IsNullOrWhiteSpace(request.EndDateLocal))
        {
            endDate = startDate.AddDays(RecurrenceLimits.DefaultDurationDays);
        }
        else if (!DateOnly.TryParseExact(request.EndDateLocal.Trim(), "yyyy-MM-dd",
                     CultureInfo.InvariantCulture, DateTimeStyles.None, out endDate))
        {
            return Result.Failure<RecurrencePlan>(RecurrenceMessages.EndDateMalformed, ErrorCodes.ValidationError);
        }

        if (endDate < startDate)
            return Result.Failure<RecurrencePlan>(RecurrenceMessages.EndDateBeforeStart, ErrorCodes.ValidationError);

        if (endDate > startDate.AddDays(RecurrenceLimits.MaxDurationDays))
            return Result.Failure<RecurrencePlan>(
                string.Format(CultureInfo.InvariantCulture, RecurrenceMessages.EndDateTooFar, RecurrenceLimits.MaxDurationDays),
                ErrorCodes.ValidationError);

        var shape = ResolveShape(type, request, startDate);
        if (!shape.IsSuccess)
            return Result.Failure<RecurrencePlan>(shape.Error!, shape.ErrorCode);

        // Interval is fixed at 1 because the rules the UI can express are "every day", "every
        // week on these days" and "every month on this date". The column accepts other values so
        // a future "every 2 weeks" is application code, not a migration.
        return Result.Success(new RecurrencePlan(
            type, 1, startTime, timeZone, startDate, endDate,
            shape.Value!.ByWeekdays, shape.Value!.ByMonthDay));
    }

    /// <summary>
    /// The cadence-specific half of the rule: which weekdays, or which day of the month.
    ///
    /// Two rules, both deliberate:
    ///  - A field belonging to another cadence is REFUSED, never ignored. Weekdays on a monthly
    ///    repeat means the client and the server disagree about what was booked, and the one thing
    ///    worse than telling the user is not telling them.
    ///  - An ABSENT field for the cadence's own shape is defaulted from the start date, because
    ///    "weekly from Tuesday the 12th" has exactly one sane reading and refusing it would make
    ///    the client send back a value it just derived itself.
    /// </summary>
    private static Result<RecurrenceShape> ResolveShape(string type, RecurrenceRequest request, DateOnly startDate)
    {
        var weekdaysGiven = request.ByWeekdays is { Count: > 0 };
        var monthDayGiven = request.ByMonthDay.HasValue;

        if (type != RecurrenceTypes.Weekly && weekdaysGiven)
            return Result.Failure<RecurrenceShape>(RecurrenceMessages.WeekdaysNotApplicable, ErrorCodes.ValidationError);

        if (type != RecurrenceTypes.Monthly && monthDayGiven)
            return Result.Failure<RecurrenceShape>(RecurrenceMessages.MonthDayNotApplicable, ErrorCodes.ValidationError);

        switch (type)
        {
            case RecurrenceTypes.Weekly:
            {
                if (!weekdaysGiven)
                    return Result.Success(new RecurrenceShape(new[] { IsoWeekdays.Of(startDate) }, null));

                // Out-of-range values are refused rather than filtered out: a client that sent 0
                // or 8 has an off-by-one somewhere, and silently booking the days it got right
                // hides it until somebody misses the day it got wrong.
                foreach (var weekday in request.ByWeekdays!)
                {
                    if (!IsoWeekdays.IsValid(weekday))
                        return Result.Failure<RecurrenceShape>(RecurrenceMessages.WeekdayOutOfRange, ErrorCodes.ValidationError);
                }

                var normalized = RecurrenceScheduleCalculator.NormalizeWeekdays(request.ByWeekdays);
                if (normalized is null)
                    return Result.Failure<RecurrenceShape>(RecurrenceMessages.WeekdayOutOfRange, ErrorCodes.ValidationError);

                return Result.Success(new RecurrenceShape(normalized, null));
            }

            case RecurrenceTypes.Monthly:
            {
                if (!monthDayGiven)
                    return Result.Success(new RecurrenceShape(null, startDate.Day));

                var dayOfMonth = request.ByMonthDay!.Value;
                if (dayOfMonth < 1 || dayOfMonth > 31)
                    return Result.Failure<RecurrenceShape>(RecurrenceMessages.MonthDayOutOfRange, ErrorCodes.ValidationError);

                return Result.Success(new RecurrenceShape(null, dayOfMonth));
            }

            default:
                return Result.Success(new RecurrenceShape(null, null));
        }
    }

    private sealed record RecurrenceShape(IReadOnlyList<int>? ByWeekdays, int? ByMonthDay);
}
