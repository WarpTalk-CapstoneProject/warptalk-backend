using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// Period maths shared by every admin insights endpoint. Lives here alongside the other
/// WarpTalk.Shared tests.
/// </summary>
public sealed class AdminComparisonRangeTests
{
    private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static AdminDateRange Range(DateTime from, DateTime to) => new() { From = from, To = to };

    [Fact]
    public void Previous_IsTheSameLengthWindowImmediatelyBefore()
    {
        var (from, to) = AdminComparisonRange.PreviousPeriodOf(Utc(2026, 9, 8), Utc(2026, 9, 15));

        Assert.Equal(Utc(2026, 9, 1), from);
        Assert.Equal(Utc(2026, 9, 8), to);
    }

    [Fact]
    public void Previous_KeepsPartialDays()
    {
        var (from, to) = AdminComparisonRange.PreviousPeriodOf(Utc(2026, 9, 1), Utc(2026, 9, 17, 12));

        Assert.Equal(Utc(2026, 8, 15, 12), from);
        Assert.Equal(Utc(2026, 9, 1), to);
    }

    [Fact]
    public void PreviousMonth_ShiftsAFullMonthOntoTheFullPreviousMonth()
    {
        var (from, to) = AdminComparisonRange.PreviousMonthOf(Utc(2026, 3, 1), Utc(2026, 4, 1), TimeZoneInfo.Utc);

        Assert.Equal(Utc(2026, 2, 1), from);
        Assert.Equal(Utc(2026, 3, 1), to);
    }

    [Fact]
    public void PreviousMonth_ComparesMonthToDateWithTheSameDaysLastMonth()
    {
        var (from, to) = AdminComparisonRange.PreviousMonthOf(Utc(2026, 9, 1), Utc(2026, 9, 17, 10), TimeZoneInfo.Utc);

        Assert.Equal(Utc(2026, 8, 1), from);
        Assert.Equal(Utc(2026, 8, 17, 10), to);
    }

    [Fact]
    public void PreviousMonth_ClampsTheStartDownToTheLastDayOfAShorterMonth()
    {
        var (from, to) = AdminComparisonRange.PreviousMonthOf(Utc(2026, 3, 31), Utc(2026, 4, 1), TimeZoneInfo.Utc);

        Assert.Equal(Utc(2026, 2, 28), from);
        Assert.Equal(Utc(2026, 3, 1), to);
    }

    [Fact]
    public void PreviousMonth_ClampsTheExclusiveEndUpSoTheWindowIsNeverEmpty()
    {
        // 30 Mar has no counterpart in February 2026; the window must still cover 28 Feb.
        var (from, to) = AdminComparisonRange.PreviousMonthOf(Utc(2026, 3, 30), Utc(2026, 3, 31), TimeZoneInfo.Utc);

        Assert.Equal(Utc(2026, 2, 28), from);
        Assert.Equal(Utc(2026, 3, 1), to);
        Assert.True(from < to);
    }

    [Fact]
    public void PreviousMonth_ClampsMonthToDateOnTheLastDayIntoAShortMonth()
    {
        // Month-to-date through 30 Mar (end 31 Mar 00:00) compares with all of February.
        var (from, to) = AdminComparisonRange.PreviousMonthOf(Utc(2026, 3, 1), Utc(2026, 3, 31), TimeZoneInfo.Utc);

        Assert.Equal(Utc(2026, 2, 1), from);
        Assert.Equal(Utc(2026, 3, 1), to);
    }

    [Fact]
    public void PreviousMonth_HandlesLeapFebruary()
    {
        var (from, to) = AdminComparisonRange.PreviousMonthOf(Utc(2028, 3, 30), Utc(2028, 3, 31), TimeZoneInfo.Utc);

        Assert.Equal(Utc(2028, 2, 29), from);
        Assert.Equal(Utc(2028, 3, 1), to);
    }

    [Fact]
    public void PreviousMonth_CrossesTheYearBoundary()
    {
        var (from, to) = AdminComparisonRange.PreviousMonthOf(Utc(2026, 1, 1), Utc(2026, 1, 15), TimeZoneInfo.Utc);

        Assert.Equal(Utc(2025, 12, 1), from);
        Assert.Equal(Utc(2025, 12, 15), to);
    }

    [Fact]
    public void TryResolve_DefaultsToPrevious()
    {
        Assert.True(AdminComparisonRange.TryResolve(
            Range(Utc(2026, 9, 8), Utc(2026, 9, 15)), null, "UTC", out var window, out var error));

        Assert.Null(error);
        Assert.Equal(AdminComparisonRange.Previous, window.Compare);
        Assert.Equal(new AdminInsightRange(Utc(2026, 9, 8), Utc(2026, 9, 15)), window.Range);
        Assert.Equal(new AdminInsightRange(Utc(2026, 9, 1), Utc(2026, 9, 8)), window.PreviousRange);
    }

    [Fact]
    public void TryResolve_AcceptsPreviousMonthCaseInsensitively()
    {
        Assert.True(AdminComparisonRange.TryResolve(
            Range(Utc(2026, 9, 1), Utc(2026, 9, 17)), "PREVIOUSMONTH", "UTC", out var window, out _));

        Assert.Equal(AdminComparisonRange.PreviousMonth, window.Compare);
        Assert.Equal(new AdminInsightRange(Utc(2026, 8, 1), Utc(2026, 8, 17)), window.PreviousRange);
    }

    [Fact]
    public void TryResolve_RejectsAnUnknownCompare()
    {
        Assert.False(AdminComparisonRange.TryResolve(
            Range(Utc(2026, 9, 1), Utc(2026, 9, 17)), "lastYear", "UTC", out _, out var error));

        Assert.Contains("compare", error);
    }

    [Fact]
    public void TryResolve_RejectsABackwardsWindow()
    {
        Assert.False(AdminComparisonRange.TryResolve(
            Range(Utc(2026, 9, 17), Utc(2026, 9, 1)), null, "UTC", out _, out var error));

        Assert.NotNull(error);
    }

    [Fact]
    public void TryResolve_RejectsAWindowLongerThan366Days()
    {
        Assert.False(AdminComparisonRange.TryResolve(
            Range(Utc(2025, 1, 1), Utc(2026, 1, 3)), null, "UTC", out _, out var error));

        Assert.Contains("366", error);
    }

    [Fact]
    public void TryResolve_Accepts366Days()
    {
        Assert.True(AdminComparisonRange.TryResolve(
            Range(Utc(2024, 1, 1), Utc(2025, 1, 1)), null, "UTC", out _, out _));
    }

    [Fact]
    public void DaysOf_ListsEveryTouchedUtcDay()
    {
        var days = AdminComparisonRange.DaysOf(Utc(2026, 9, 1, 12), Utc(2026, 9, 4), TimeZoneInfo.Utc);

        Assert.Equal([Utc(2026, 9, 1), Utc(2026, 9, 2), Utc(2026, 9, 3)], days.Select(d => d.Start));
        Assert.Equal(["2026-09-01", "2026-09-02", "2026-09-03"], days.Select(d => d.Key));
    }

    // ── time zone ────────────────────────────────────────────────────────────

    private static readonly TimeZoneInfo Vietnam = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

    [Fact]
    public void TryResolve_DefaultsToTheVietnamCalendar()
    {
        Assert.True(AdminComparisonRange.TryResolve(
            new AdminInsightsQuery { From = Utc(2026, 9, 8), To = Utc(2026, 9, 15) }, out var window, out var error));

        Assert.Null(error);
        Assert.Equal(AdminComparisonRange.DefaultTimeZoneId, window.TimeZone.Id);
        Assert.Equal(TimeSpan.FromHours(7), window.TimeZone.GetUtcOffset(Utc(2026, 9, 8)));
    }

    [Fact]
    public void TryResolve_ReadsTheQuerysTz()
    {
        Assert.True(AdminComparisonRange.TryResolve(
            new AdminInsightsQuery { From = Utc(2026, 9, 8), To = Utc(2026, 9, 15), Tz = " Europe/Berlin " },
            out var window,
            out _));

        Assert.Equal("Europe/Berlin", window.TimeZone.Id);
    }

    [Theory]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("../../etc/passwd")]
    [InlineData("/etc/localtime")]
    [InlineData("+07:00")]
    [InlineData("Asia/Ho Chi Minh")]
    public void TryResolve_RejectsAnUnknownTz(string tz)
    {
        Assert.False(AdminComparisonRange.TryResolve(
            new AdminInsightsQuery { From = Utc(2026, 9, 8), To = Utc(2026, 9, 15), Tz = tz }, out _, out var error));

        Assert.Contains("tz", error);
    }

    [Fact]
    public void DaysOf_OnTheVietnamCalendar_StartsEachDayAt1700Utc()
    {
        // 16:59Z on 1 Sep is 23:59 local; 17:00Z is already 2 Sep there.
        var days = AdminComparisonRange.DaysOf(Utc(2026, 9, 1, 16), Utc(2026, 9, 2, 18), Vietnam);

        Assert.Equal(["2026-09-01", "2026-09-02", "2026-09-03"], days.Select(d => d.Key));
        Assert.Equal(Utc(2026, 8, 31, 17), days[0].Start);
        Assert.Equal(Utc(2026, 9, 1, 17), days[0].End);
        Assert.Equal(Utc(2026, 9, 1, 17), days[1].Start);

        Assert.Equal(0, AdminComparisonRange.IndexOfDay(days, new DateTime(2026, 9, 1, 16, 59, 59, DateTimeKind.Utc)));
        Assert.Equal(1, AdminComparisonRange.IndexOfDay(days, Utc(2026, 9, 1, 17)));
        Assert.Equal(-1, AdminComparisonRange.IndexOfDay(days, Utc(2026, 9, 3, 17)));
        Assert.Equal(-1, AdminComparisonRange.IndexOfDay(days, Utc(2026, 8, 31, 16)));
    }

    [Fact]
    public void DaysOf_AVietnamMonth_HasItsOwnThirtyDays()
    {
        var days = AdminComparisonRange.DaysOf(Utc(2026, 8, 31, 17), Utc(2026, 9, 30, 17), Vietnam);

        Assert.Equal(30, days.Count);
        Assert.Equal("2026-09-01", days[0].Key);
        Assert.Equal("2026-09-30", days[^1].Key);
    }

    [Fact]
    public void PreviousMonth_OfAVietnamMonth_IsTheVietnamMonthBefore()
    {
        // Vietnam's September → Vietnam's August, both as local-midnight instants.
        var (from, to) = AdminComparisonRange.PreviousMonthOf(Utc(2026, 8, 31, 17), Utc(2026, 9, 30, 17), Vietnam);

        Assert.Equal(Utc(2026, 7, 31, 17), from);
        Assert.Equal(Utc(2026, 8, 31, 17), to);
    }

    [Fact]
    public void PreviousMonth_OfAVietnamMonthToDate_IsTheSameLocalDaysLastMonth()
    {
        // 1 Sep 00:00 → 17 Sep 17:30 local.
        var (from, to) = AdminComparisonRange.PreviousMonthOf(Utc(2026, 8, 31, 17), Utc(2026, 9, 17, 10, 30), Vietnam);

        Assert.Equal(Utc(2026, 7, 31, 17), from);
        Assert.Equal(Utc(2026, 8, 17, 10, 30), to);
    }

    [Fact]
    public void PreviousMonth_ClampsOnTheLocalCalendar()
    {
        // Vietnam's 31 Mar compares with Vietnam's 28 Feb (exclusive end clamps up to local 1 Mar).
        var (from, to) = AdminComparisonRange.PreviousMonthOf(Utc(2026, 3, 30, 17), Utc(2026, 3, 31, 17), Vietnam);
        Assert.Equal(Utc(2026, 2, 27, 17), from);
        Assert.Equal(Utc(2026, 2, 28, 17), to);

        // The same instants on the UTC calendar collapse to a seven-hour sliver — the bug.
        var (utcFrom, utcTo) = AdminComparisonRange.PreviousMonthOf(Utc(2026, 3, 30, 17), Utc(2026, 3, 31, 17), TimeZoneInfo.Utc);
        Assert.Equal(TimeSpan.FromHours(7), utcTo - utcFrom);
    }

    [Fact]
    public void Previous_IgnoresTheTimeZone()
    {
        Assert.True(AdminComparisonRange.TryResolve(
            Range(Utc(2026, 8, 31, 17), Utc(2026, 9, 17, 10)), null, "Asia/Ho_Chi_Minh", out var window, out _));

        Assert.Equal(window.To - window.From, window.PreviousTo - window.PreviousFrom);
        Assert.Equal(window.From, window.PreviousTo);
    }

    [Fact]
    public void DaysOf_AcrossADaylightSavingStart_HasAShortDay()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        // 8 Mar 2026: clocks jump 02:00 → 03:00 in New York.
        var days = AdminComparisonRange.DaysOf(Utc(2026, 3, 7, 5), Utc(2026, 3, 9, 4), newYork);

        Assert.Equal(["2026-03-07", "2026-03-08"], days.Select(d => d.Key));
        Assert.Equal(TimeSpan.FromHours(23), days[1].End - days[1].Start);
        Assert.Equal(Utc(2026, 3, 8, 5), days[1].Start);
    }

    [Fact]
    public void LocalDayOf_IsTheAdminsToday()
    {
        var today = AdminComparisonRange.LocalDayOf(Utc(2026, 9, 17, 18), Vietnam);

        Assert.Equal(new DateOnly(2026, 9, 18), today.Date);
        Assert.Equal(Utc(2026, 9, 17, 17), today.Start);
        Assert.Equal(Utc(2026, 9, 18, 17), today.End);
    }

    [Fact]
    public void MonthsEnding_IsSixVietnamMonthsEndingWithTheMonthOfTo()
    {
        // 23 Sep 2026 10:00 in Vietnam.
        var months = AdminComparisonRange.MonthsEnding(Utc(2026, 9, 23, 3), Vietnam);

        Assert.Equal(["2026-04", "2026-05", "2026-06", "2026-07", "2026-08", "2026-09"], months.Select(m => m.Key));
        // A Vietnam month starts at 17:00Z the day before.
        Assert.Equal(Utc(2026, 3, 31, 17), months[0].Start);
        Assert.Equal(Utc(2026, 9, 30, 17), months[^1].End);
        Assert.All(months.Zip(months.Skip(1)), pair => Assert.Equal(pair.First.End, pair.Second.Start));
    }

    [Fact]
    public void MonthsEnding_AnExclusiveToOnAMonthBoundaryEndsWithThePreviousMonth()
    {
        // `to` is exclusive: 1 Oct 00:00 Vietnam is the end of September, not a day of October.
        var months = AdminComparisonRange.MonthsEnding(Utc(2026, 9, 30, 17), Vietnam, count: 2);

        Assert.Equal(["2026-08", "2026-09"], months.Select(m => m.Key));
    }
}
