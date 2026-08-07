using System;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Domain.Constants;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

/// <summary>
/// WT-327. What the server does with what the Daily modal sends: the defaults, and the two
/// refusals that keep a series from being unbounded.
/// </summary>
public class RecurrencePlannerTests
{
    private const string Hcm = "Asia/Ho_Chi_Minh";

    // 2026-08-06 03:00 UTC == 2026-08-06 10:00 in Ho Chi Minh City.
    private static readonly DateTime TenAmLocal = new(2026, 8, 6, 3, 0, 0, DateTimeKind.Utc);

    private static RecurrenceRequest Daily(string time = "08:00", string? start = null, string? end = null) =>
        new(RecurrenceTypes.Daily, time, Hcm, start, end);

    [Fact]
    public void An_eight_am_daily_booked_at_ten_am_starts_tomorrow()
    {
        // The single most reachable mistake this feature could make: booking "daily at 8am"
        // during the working day and getting a room for 8am this morning.
        var plan = RecurrencePlanner.Plan(Daily(), TenAmLocal);

        plan.IsSuccess.Should().BeTrue();
        plan.Value!.StartDate.Should().Be(new DateOnly(2026, 8, 7));
        plan.Value.StartTimeLocal.Should().Be(new TimeOnly(8, 0));
        plan.Value.TimeZone.Id.Should().Be(Hcm);
    }

    [Fact]
    public void An_eleven_pm_daily_booked_at_ten_am_starts_today()
    {
        var plan = RecurrencePlanner.Plan(Daily("23:00"), TenAmLocal);

        plan.IsSuccess.Should().BeTrue();
        plan.Value!.StartDate.Should().Be(new DateOnly(2026, 8, 6));
    }

    [Fact]
    public void The_local_date_is_read_in_the_series_zone_not_UTC()
    {
        // 2026-08-06 18:00 UTC is already the 7th in Ho Chi Minh City, and 08:00 on the 7th has
        // not happened there yet — so the series starts on the 7th, not the 8th.
        var plan = RecurrencePlanner.Plan(Daily(), new DateTime(2026, 8, 6, 18, 0, 0, DateTimeKind.Utc));

        plan.IsSuccess.Should().BeTrue();
        plan.Value!.StartDate.Should().Be(new DateOnly(2026, 8, 7));
    }

    [Fact]
    public void An_omitted_end_date_is_a_default_not_forever()
    {
        // Something must stop a series generating rooms for an abandoned workspace. This is it.
        var plan = RecurrencePlanner.Plan(Daily(), TenAmLocal);

        plan.IsSuccess.Should().BeTrue();
        plan.Value!.EndDate.Should().Be(
            plan.Value.StartDate.AddDays(RecurrenceLimits.DefaultDurationDays));
    }

    [Fact]
    public void An_end_date_beyond_the_ceiling_is_refused_rather_than_clamped()
    {
        // Refused, not silently clamped: a host who asked for two years and got one must be
        // told, or the dead-switch problem simply moves to a different field.
        var plan = RecurrencePlanner.Plan(Daily(end: "2028-08-06"), TenAmLocal);

        plan.IsSuccess.Should().BeFalse();
        plan.Error.Should().Contain(RecurrenceLimits.MaxDurationDays.ToString());
    }

    [Fact]
    public void An_end_date_before_the_start_is_refused()
    {
        var plan = RecurrencePlanner.Plan(Daily(start: "2026-08-10", end: "2026-08-09"), TenAmLocal);

        plan.IsSuccess.Should().BeFalse();
        plan.Error.Should().Be(RecurrenceMessages.EndDateBeforeStart);
    }

    [Fact]
    public void A_start_date_in_the_past_is_refused()
    {
        var plan = RecurrencePlanner.Plan(Daily(start: "2026-08-01"), TenAmLocal);

        plan.IsSuccess.Should().BeFalse();
        plan.Error.Should().Be(RecurrenceMessages.StartDateInPast);
    }

    [Theory]
    [InlineData("8:00")]      // not zero-padded
    [InlineData("08:00:00")]  // seconds
    [InlineData("8am")]
    [InlineData("")]
    [InlineData(null)]
    public void A_malformed_time_is_a_validation_failure(string? time)
    {
        var plan = RecurrencePlanner.Plan(new RecurrenceRequest(RecurrenceTypes.Daily, time!, Hcm), TenAmLocal);

        plan.IsSuccess.Should().BeFalse();
        plan.Error.Should().Be(RecurrenceMessages.TimeMalformed);
        plan.ErrorCode.Should().Be(WarpTalk.Shared.ErrorCodes.ValidationError);
    }

    [Fact]
    public void An_unknown_time_zone_is_a_validation_failure_not_a_crash()
    {
        var plan = RecurrencePlanner.Plan(
            new RecurrenceRequest(RecurrenceTypes.Daily, "08:00", "Mars/Olympus_Mons"), TenAmLocal);

        plan.IsSuccess.Should().BeFalse();
        plan.Error.Should().Be(RecurrenceMessages.TimeZoneUnknown);
    }

    [Theory]
    [InlineData("WEEKLY")]
    [InlineData("MONTHLY")]
    public void Weekly_and_monthly_are_refused_out_loud(string type)
    {
        // The database will store them and the enumerator knows the names, but nothing
        // materialises them yet — so accepting one would recreate the exact failure this
        // feature removed: a control that looks like it worked and did nothing.
        var plan = RecurrencePlanner.Plan(new RecurrenceRequest(type, "08:00", Hcm), TenAmLocal);

        plan.IsSuccess.Should().BeFalse();
        plan.Error.Should().Contain("not available yet");
    }

    [Fact]
    public void An_unrecognised_repeat_option_is_refused()
    {
        var plan = RecurrencePlanner.Plan(new RecurrenceRequest("FORTNIGHTLY", "08:00", Hcm), TenAmLocal);

        plan.IsSuccess.Should().BeFalse();
        plan.Error.Should().Be(RecurrenceMessages.TypeUnrecognised);
    }

    [Fact]
    public void Type_spelling_is_normalised()
    {
        var plan = RecurrencePlanner.Plan(new RecurrenceRequest("daily", "08:00", Hcm), TenAmLocal);

        plan.IsSuccess.Should().BeTrue();
        plan.Value!.Type.Should().Be(RecurrenceTypes.Daily);
    }
}
