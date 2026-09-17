using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// Period maths shared by every admin insights endpoint. Lives here alongside the other
/// WarpTalk.Shared tests.
/// </summary>
public sealed class AdminComparisonRangeTests
{
    private static DateTime Utc(int year, int month, int day, int hour = 0) =>
        new(year, month, day, hour, 0, 0, DateTimeKind.Utc);

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
        var (from, to) = AdminComparisonRange.PreviousMonthOf(Utc(2026, 3, 1), Utc(2026, 4, 1));

        Assert.Equal(Utc(2026, 2, 1), from);
        Assert.Equal(Utc(2026, 3, 1), to);
    }

    [Fact]
    public void PreviousMonth_ComparesMonthToDateWithTheSameDaysLastMonth()
    {
        var (from, to) = AdminComparisonRange.PreviousMonthOf(Utc(2026, 9, 1), Utc(2026, 9, 17, 10));

        Assert.Equal(Utc(2026, 8, 1), from);
        Assert.Equal(Utc(2026, 8, 17, 10), to);
    }

    [Fact]
    public void PreviousMonth_ClampsTheStartDownToTheLastDayOfAShorterMonth()
    {
        var (from, to) = AdminComparisonRange.PreviousMonthOf(Utc(2026, 3, 31), Utc(2026, 4, 1));

        Assert.Equal(Utc(2026, 2, 28), from);
        Assert.Equal(Utc(2026, 3, 1), to);
    }

    [Fact]
    public void PreviousMonth_ClampsTheExclusiveEndUpSoTheWindowIsNeverEmpty()
    {
        // 30 Mar has no counterpart in February 2026; the window must still cover 28 Feb.
        var (from, to) = AdminComparisonRange.PreviousMonthOf(Utc(2026, 3, 30), Utc(2026, 3, 31));

        Assert.Equal(Utc(2026, 2, 28), from);
        Assert.Equal(Utc(2026, 3, 1), to);
        Assert.True(from < to);
    }

    [Fact]
    public void PreviousMonth_ClampsMonthToDateOnTheLastDayIntoAShortMonth()
    {
        // Month-to-date through 30 Mar (end 31 Mar 00:00) compares with all of February.
        var (from, to) = AdminComparisonRange.PreviousMonthOf(Utc(2026, 3, 1), Utc(2026, 3, 31));

        Assert.Equal(Utc(2026, 2, 1), from);
        Assert.Equal(Utc(2026, 3, 1), to);
    }

    [Fact]
    public void PreviousMonth_HandlesLeapFebruary()
    {
        var (from, to) = AdminComparisonRange.PreviousMonthOf(Utc(2028, 3, 30), Utc(2028, 3, 31));

        Assert.Equal(Utc(2028, 2, 29), from);
        Assert.Equal(Utc(2028, 3, 1), to);
    }

    [Fact]
    public void PreviousMonth_CrossesTheYearBoundary()
    {
        var (from, to) = AdminComparisonRange.PreviousMonthOf(Utc(2026, 1, 1), Utc(2026, 1, 15));

        Assert.Equal(Utc(2025, 12, 1), from);
        Assert.Equal(Utc(2025, 12, 15), to);
    }

    [Fact]
    public void TryResolve_DefaultsToPrevious()
    {
        Assert.True(AdminComparisonRange.TryResolve(
            Range(Utc(2026, 9, 8), Utc(2026, 9, 15)), null, out var window, out var error));

        Assert.Null(error);
        Assert.Equal(AdminComparisonRange.Previous, window.Compare);
        Assert.Equal(new AdminInsightRange(Utc(2026, 9, 8), Utc(2026, 9, 15)), window.Range);
        Assert.Equal(new AdminInsightRange(Utc(2026, 9, 1), Utc(2026, 9, 8)), window.PreviousRange);
    }

    [Fact]
    public void TryResolve_AcceptsPreviousMonthCaseInsensitively()
    {
        Assert.True(AdminComparisonRange.TryResolve(
            Range(Utc(2026, 9, 1), Utc(2026, 9, 17)), "PREVIOUSMONTH", out var window, out _));

        Assert.Equal(AdminComparisonRange.PreviousMonth, window.Compare);
        Assert.Equal(new AdminInsightRange(Utc(2026, 8, 1), Utc(2026, 8, 17)), window.PreviousRange);
    }

    [Fact]
    public void TryResolve_RejectsAnUnknownCompare()
    {
        Assert.False(AdminComparisonRange.TryResolve(
            Range(Utc(2026, 9, 1), Utc(2026, 9, 17)), "lastYear", out _, out var error));

        Assert.Contains("compare", error);
    }

    [Fact]
    public void TryResolve_RejectsABackwardsWindow()
    {
        Assert.False(AdminComparisonRange.TryResolve(
            Range(Utc(2026, 9, 17), Utc(2026, 9, 1)), null, out _, out var error));

        Assert.NotNull(error);
    }

    [Fact]
    public void TryResolve_RejectsAWindowLongerThan366Days()
    {
        Assert.False(AdminComparisonRange.TryResolve(
            Range(Utc(2025, 1, 1), Utc(2026, 1, 3)), null, out _, out var error));

        Assert.Contains("366", error);
    }

    [Fact]
    public void TryResolve_Accepts366Days()
    {
        Assert.True(AdminComparisonRange.TryResolve(
            Range(Utc(2024, 1, 1), Utc(2025, 1, 1)), null, out _, out _));
    }

    [Fact]
    public void DaysOf_ListsEveryTouchedUtcDay()
    {
        var days = AdminComparisonRange.DaysOf(Utc(2026, 9, 1, 12), Utc(2026, 9, 4));

        Assert.Equal([Utc(2026, 9, 1), Utc(2026, 9, 2), Utc(2026, 9, 3)], days);
    }
}
