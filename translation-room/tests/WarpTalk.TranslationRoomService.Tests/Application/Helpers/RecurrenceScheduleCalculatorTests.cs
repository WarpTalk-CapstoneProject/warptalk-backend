using System;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Domain.Constants;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

/// <summary>
/// WT-327. The date arithmetic behind a recurring booking, pinned here rather than discovered in
/// a worker: every interesting failure of this feature is an off-by-one day or a time zone, and
/// none of them should need a Postgres container to reproduce.
/// </summary>
public class RecurrenceScheduleCalculatorTests
{
    private static readonly TimeZoneInfo HoChiMinh =
        RecurrenceScheduleCalculator.ResolveTimeZone("Asia/Ho_Chi_Minh")!;

    [Fact]
    public void Resolves_the_teams_own_time_zone()
    {
        HoChiMinh.Should().NotBeNull();
    }

    [Fact]
    public void Unknown_time_zone_is_a_null_not_an_exception()
    {
        // A bad zone is user input, so it has to become a validation failure, not a 500.
        RecurrenceScheduleCalculator.ResolveTimeZone("Mars/Olympus_Mons").Should().BeNull();
        RecurrenceScheduleCalculator.ResolveTimeZone("").Should().BeNull();
        RecurrenceScheduleCalculator.ResolveTimeZone(null).Should().BeNull();
    }

    [Fact]
    public void Eight_am_in_Ho_Chi_Minh_City_is_one_am_UTC()
    {
        // The whole point of the feature: "8am daily" means 8am where the team is. UTC+7, no DST.
        var utc = RecurrenceScheduleCalculator.ToUtcInstant(
            new DateOnly(2026, 8, 10), new TimeOnly(8, 0), HoChiMinh);

        utc.Should().Be(new DateTime(2026, 8, 10, 1, 0, 0, DateTimeKind.Utc));
        utc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Local_time_stays_the_same_across_a_zone_that_does_observe_DST()
    {
        // Vietnam has no DST, so the guarantee is demonstrated where it can actually be seen:
        // in New York, 08:00 local is 12:00 UTC in summer and 13:00 UTC in winter. Storing the
        // wall clock plus a zone is what keeps both of those "8am" — a stored UTC instant, or a
        // stored fixed offset, would drift by an hour half the year.
        var newYork = RecurrenceScheduleCalculator.ResolveTimeZone("America/New_York")!;

        var summer = RecurrenceScheduleCalculator.ToUtcInstant(
            new DateOnly(2026, 7, 1), new TimeOnly(8, 0), newYork);
        var winter = RecurrenceScheduleCalculator.ToUtcInstant(
            new DateOnly(2026, 12, 1), new TimeOnly(8, 0), newYork);

        summer.Hour.Should().Be(12);
        winter.Hour.Should().Be(13);
    }

    [Fact]
    public void A_wall_clock_swallowed_by_a_spring_forward_gap_still_produces_a_meeting()
    {
        // 2026-03-08 02:30 does not exist in New York. Skipping the day would silently drop a
        // meeting the host booked, so the occurrence moves to the first instant that does exist.
        var newYork = RecurrenceScheduleCalculator.ResolveTimeZone("America/New_York")!;

        var utc = RecurrenceScheduleCalculator.ToUtcInstant(
            new DateOnly(2026, 3, 8), new TimeOnly(2, 30), newYork);

        utc.Kind.Should().Be(DateTimeKind.Utc);
        // 03:00 EDT on the transition day == 07:00 UTC.
        utc.Should().Be(new DateTime(2026, 3, 8, 7, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void An_ambiguous_wall_clock_takes_the_earlier_of_the_two_instants()
    {
        // 2026-11-01 01:30 happens twice in New York. The earlier one is chosen so nobody
        // arrives to find the meeting already an hour over.
        var newYork = RecurrenceScheduleCalculator.ResolveTimeZone("America/New_York")!;

        var utc = RecurrenceScheduleCalculator.ToUtcInstant(
            new DateOnly(2026, 11, 1), new TimeOnly(1, 30), newYork);

        // 01:30 EDT (UTC-4) == 05:30 UTC; the later candidate would be 06:30 UTC.
        utc.Should().Be(new DateTime(2026, 11, 1, 5, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Local_date_is_read_in_the_series_own_zone_not_the_hosts()
    {
        // 2026-08-09 18:00 UTC is already the 10th in Ho Chi Minh City. A horizon computed in
        // the server's zone would be a day out for exactly this reason.
        var localDate = RecurrenceScheduleCalculator.LocalDateOf(
            new DateTime(2026, 8, 9, 18, 0, 0, DateTimeKind.Utc), HoChiMinh);

        localDate.Should().Be(new DateOnly(2026, 8, 10));
    }

    [Fact]
    public void First_pass_enumerates_from_the_start_date_up_to_the_horizon()
    {
        var dates = RecurrenceScheduleCalculator.EnumerateOccurrenceDates(
            RecurrenceTypes.Daily,
            interval: 1,
            startsOn: new DateOnly(2026, 8, 10),
            endsOn: new DateOnly(2026, 9, 9),
            alreadyMaterializedThrough: null,
            horizonThrough: new DateOnly(2026, 8, 14),
            maxCount: 32);

        dates.Should().HaveCount(5);
        dates[0].Should().Be(new DateOnly(2026, 8, 10));
        dates[^1].Should().Be(new DateOnly(2026, 8, 14));
    }

    [Fact]
    public void The_horizon_rolls_forward_from_the_watermark_never_back_over_it()
    {
        // This is the property that makes the sweep safe to run over and over, and the reason a
        // single cancelled occurrence stays cancelled: generation resumes strictly AFTER the
        // watermark, so a date already passed is never revisited.
        var dates = RecurrenceScheduleCalculator.EnumerateOccurrenceDates(
            RecurrenceTypes.Daily,
            interval: 1,
            startsOn: new DateOnly(2026, 8, 10),
            endsOn: new DateOnly(2026, 9, 9),
            alreadyMaterializedThrough: new DateOnly(2026, 8, 14),
            horizonThrough: new DateOnly(2026, 8, 17),
            maxCount: 32);

        dates.Should().Equal(
            new DateOnly(2026, 8, 15),
            new DateOnly(2026, 8, 16),
            new DateOnly(2026, 8, 17));
    }

    [Fact]
    public void A_sweep_that_has_caught_up_to_the_horizon_creates_nothing()
    {
        var dates = RecurrenceScheduleCalculator.EnumerateOccurrenceDates(
            RecurrenceTypes.Daily, 1,
            startsOn: new DateOnly(2026, 8, 10),
            endsOn: new DateOnly(2026, 9, 9),
            alreadyMaterializedThrough: new DateOnly(2026, 8, 24),
            horizonThrough: new DateOnly(2026, 8, 24),
            maxCount: 32);

        dates.Should().BeEmpty();
    }

    [Fact]
    public void Generation_never_runs_past_the_series_end_date()
    {
        // The end condition is what stops an abandoned demo workspace generating rooms forever.
        var dates = RecurrenceScheduleCalculator.EnumerateOccurrenceDates(
            RecurrenceTypes.Daily, 1,
            startsOn: new DateOnly(2026, 8, 10),
            endsOn: new DateOnly(2026, 8, 12),
            alreadyMaterializedThrough: null,
            horizonThrough: new DateOnly(2026, 8, 31),
            maxCount: 32);

        dates.Should().HaveCount(3);
        dates[^1].Should().Be(new DateOnly(2026, 8, 12));
    }

    [Fact]
    public void One_pass_is_capped()
    {
        var dates = RecurrenceScheduleCalculator.EnumerateOccurrenceDates(
            RecurrenceTypes.Daily, 1,
            startsOn: new DateOnly(2026, 8, 10),
            endsOn: new DateOnly(2027, 8, 10),
            alreadyMaterializedThrough: null,
            horizonThrough: new DateOnly(2027, 8, 10),
            maxCount: 4);

        dates.Should().HaveCount(4);
    }

    [Fact]
    public void A_cadence_this_build_cannot_materialise_produces_nothing_rather_than_daily_dates()
    {
        // The guard that used to keep WEEKLY and MONTHLY inert still has to hold for whatever
        // cadence is added to the schema next: falling through to daily behaviour is far worse
        // than producing nothing, because a series quietly running at the wrong cadence is a bug
        // nobody would report as one.
        RecurrenceScheduleCalculator.EnumerateOccurrenceDates(
            "FORTNIGHTLY", 1,
            new DateOnly(2026, 8, 10), new DateOnly(2026, 9, 9),
            null, new DateOnly(2026, 8, 24), 32)
            .Should().BeEmpty();
    }

    // ── WEEKLY ────────────────────────────────────────────────────────────────

    [Fact]
    public void Weekly_yields_every_selected_weekday_in_order()
    {
        // 2026-08-10 is a Monday. Mon(1) + Wed(3) + Fri(5) over two weeks.
        var dates = RecurrenceScheduleCalculator.EnumerateOccurrenceDates(
            RecurrenceTypes.Weekly, 1,
            startsOn: new DateOnly(2026, 8, 10),
            endsOn: new DateOnly(2026, 8, 23),
            alreadyMaterializedThrough: null,
            horizonThrough: new DateOnly(2026, 8, 23),
            maxCount: 32,
            byWeekdays: new[] { 5, 1, 3 }); // deliberately unsorted

        dates.Should().Equal(
            new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 12), new DateOnly(2026, 8, 14),
            new DateOnly(2026, 8, 17), new DateOnly(2026, 8, 19), new DateOnly(2026, 8, 21));
    }

    [Fact]
    public void Weekly_with_no_weekdays_named_repeats_the_start_dates_own_weekday()
    {
        // "Weekly from Tuesday the 11th" has exactly one sane reading, and refusing it would make
        // the client send back a value it just derived itself.
        var dates = RecurrenceScheduleCalculator.EnumerateOccurrenceDates(
            RecurrenceTypes.Weekly, 1,
            startsOn: new DateOnly(2026, 8, 11), // a Tuesday
            endsOn: new DateOnly(2026, 9, 1),
            alreadyMaterializedThrough: null,
            horizonThrough: new DateOnly(2026, 9, 1),
            maxCount: 32);

        dates.Should().Equal(
            new DateOnly(2026, 8, 11), new DateOnly(2026, 8, 18),
            new DateOnly(2026, 8, 25), new DateOnly(2026, 9, 1));
    }

    [Fact]
    public void Weekly_never_yields_a_selected_day_that_falls_before_the_series_starts()
    {
        // Booking "Mondays and Fridays" on a Wednesday must not produce that same week's Monday,
        // which is in the past.
        var dates = RecurrenceScheduleCalculator.EnumerateOccurrenceDates(
            RecurrenceTypes.Weekly, 1,
            startsOn: new DateOnly(2026, 8, 12), // Wednesday
            endsOn: new DateOnly(2026, 8, 21),
            alreadyMaterializedThrough: null,
            horizonThrough: new DateOnly(2026, 8, 21),
            maxCount: 32,
            byWeekdays: new[] { 1, 5 });

        dates.Should().Equal(
            new DateOnly(2026, 8, 14),  // that week's Friday
            new DateOnly(2026, 8, 17),  // next Monday
            new DateOnly(2026, 8, 21)); // next Friday
    }

    [Fact]
    public void A_fortnightly_grid_is_anchored_on_the_week_not_on_the_start_day()
    {
        // Every 2 weeks on Mon+Fri, starting on a Friday. Anchoring the fortnight on the start
        // DAY would put the following Monday in an "off" week and drop it; the correct reading is
        // that the whole week the series starts in is an "on" week.
        var dates = RecurrenceScheduleCalculator.EnumerateOccurrenceDates(
            RecurrenceTypes.Weekly, interval: 2,
            startsOn: new DateOnly(2026, 8, 14), // Friday
            endsOn: new DateOnly(2026, 9, 30),
            alreadyMaterializedThrough: null,
            horizonThrough: new DateOnly(2026, 9, 30),
            maxCount: 32,
            byWeekdays: new[] { 1, 5 });

        dates.Should().Equal(
            new DateOnly(2026, 8, 14),  // week of Aug 10
            new DateOnly(2026, 8, 24), new DateOnly(2026, 8, 28),  // week of Aug 24
            new DateOnly(2026, 9, 7), new DateOnly(2026, 9, 11),   // week of Sep 7
            new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 25)); // week of Sep 21
    }

    [Fact]
    public void A_weekly_watermark_resumes_after_it_without_shifting_the_grid()
    {
        var dates = RecurrenceScheduleCalculator.EnumerateOccurrenceDates(
            RecurrenceTypes.Weekly, 1,
            startsOn: new DateOnly(2026, 8, 10),
            endsOn: new DateOnly(2026, 8, 31),
            alreadyMaterializedThrough: new DateOnly(2026, 8, 17),
            horizonThrough: new DateOnly(2026, 8, 31),
            maxCount: 32,
            byWeekdays: new[] { 1 });

        dates.Should().Equal(new DateOnly(2026, 8, 24), new DateOnly(2026, 8, 31));
    }

    // ── MONTHLY ───────────────────────────────────────────────────────────────

    [Fact]
    public void Monthly_repeats_on_the_same_day_of_each_month()
    {
        var dates = RecurrenceScheduleCalculator.EnumerateOccurrenceDates(
            RecurrenceTypes.Monthly, 1,
            startsOn: new DateOnly(2026, 8, 15),
            endsOn: new DateOnly(2026, 11, 30),
            alreadyMaterializedThrough: null,
            horizonThrough: new DateOnly(2026, 11, 30),
            maxCount: 32);

        dates.Should().Equal(
            new DateOnly(2026, 8, 15), new DateOnly(2026, 9, 15),
            new DateOnly(2026, 10, 15), new DateOnly(2026, 11, 15));
    }

    [Fact]
    public void A_month_too_short_for_the_chosen_day_is_skipped_not_clamped()
    {
        // "The 31st" in February means no February meeting. Clamping to the 28th would silently
        // move the meeting, and to a different day in each short month.
        var dates = RecurrenceScheduleCalculator.EnumerateOccurrenceDates(
            RecurrenceTypes.Monthly, 1,
            startsOn: new DateOnly(2026, 12, 31),
            endsOn: new DateOnly(2027, 4, 30),
            alreadyMaterializedThrough: null,
            horizonThrough: new DateOnly(2027, 4, 30),
            maxCount: 32,
            byMonthDay: 31);

        dates.Should().Equal(
            new DateOnly(2026, 12, 31),
            new DateOnly(2027, 1, 31),
            // no February, no April — neither month has a 31st
            new DateOnly(2027, 3, 31));
    }

    [Fact]
    public void A_monthly_day_earlier_than_the_start_date_begins_the_following_month()
    {
        // Booking "the 1st of every month" on the 5th: the first occurrence is next month's 1st,
        // not a date in the past.
        var dates = RecurrenceScheduleCalculator.EnumerateOccurrenceDates(
            RecurrenceTypes.Monthly, 1,
            startsOn: new DateOnly(2026, 8, 5),
            endsOn: new DateOnly(2026, 11, 30),
            alreadyMaterializedThrough: null,
            horizonThrough: new DateOnly(2026, 11, 30),
            maxCount: 32,
            byMonthDay: 1);

        dates.Should().Equal(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1));
    }

    [Fact]
    public void A_monthly_series_yields_nothing_inside_a_two_week_horizon_it_does_not_reach()
    {
        // The case that forced CreateSeriesAsync to materialise its first occurrence regardless
        // of the horizon: nothing here is wrong, there simply is no occurrence for two weeks.
        var dates = RecurrenceScheduleCalculator.EnumerateOccurrenceDates(
            RecurrenceTypes.Monthly, 1,
            startsOn: new DateOnly(2026, 8, 5),
            endsOn: new DateOnly(2027, 8, 5),
            alreadyMaterializedThrough: null,
            horizonThrough: new DateOnly(2026, 8, 19),
            maxCount: 32,
            byMonthDay: 1);

        dates.Should().BeEmpty();
    }

    [Fact]
    public void An_off_grid_watermark_snaps_forward_rather_than_shifting_the_whole_series()
    {
        var dates = RecurrenceScheduleCalculator.EnumerateOccurrenceDates(
            RecurrenceTypes.Daily,
            interval: 3,
            startsOn: new DateOnly(2026, 8, 10),
            endsOn: new DateOnly(2026, 9, 9),
            alreadyMaterializedThrough: new DateOnly(2026, 8, 14), // not on the 10/13/16 grid
            horizonThrough: new DateOnly(2026, 8, 20),
            maxCount: 32);

        dates.Should().Equal(new DateOnly(2026, 8, 16), new DateOnly(2026, 8, 19));
    }

    [Fact]
    public void A_series_is_finished_only_when_the_watermark_reaches_its_end_date()
    {
        RecurrenceScheduleCalculator
            .IsFullyMaterialized(new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 9))
            .Should().BeFalse();

        RecurrenceScheduleCalculator
            .IsFullyMaterialized(new DateOnly(2026, 9, 9), new DateOnly(2026, 9, 9))
            .Should().BeTrue();

        RecurrenceScheduleCalculator
            .IsFullyMaterialized(null, new DateOnly(2026, 9, 9))
            .Should().BeFalse();
    }
}
