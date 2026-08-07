using System;
using System.Globalization;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Domain.Constants;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>WT-327: a validated, defaulted, guaranteed-to-terminate recurrence rule.</summary>
public sealed record RecurrencePlan(
    string Type,
    int Interval,
    TimeOnly StartTimeLocal,
    TimeZoneInfo TimeZone,
    DateOnly StartDate,
    DateOnly EndDate);

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

        // Interval is fixed at 1 because the only rule the UI can express is "every day". The
        // column accepts other values so a future "every 2 days" is application code, not a
        // migration.
        return Result.Success(new RecurrencePlan(type, 1, startTime, timeZone, startDate, endDate));
    }
}
