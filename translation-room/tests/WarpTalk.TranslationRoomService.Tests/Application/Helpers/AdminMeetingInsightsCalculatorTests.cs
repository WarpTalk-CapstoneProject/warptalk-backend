using System;
using System.Linq;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

public sealed class AdminMeetingInsightsCalculatorTests
{
    private static DateTime Utc(int day, int hour = 0, int minute = 0) =>
        new(2026, 9, day, hour, minute, 0, DateTimeKind.Utc);

    private static AdminMeetingSpan Ended(DateTime start, DateTime end) => new(start, end, null, "ENDED");

    private static readonly DateTime From = Utc(8);
    private static readonly DateTime To = Utc(15);
    private static readonly DateTime Now = Utc(17);

    [Fact]
    public void MeetingsHeld_CountsOnlyMeetingsThatStartedInTheWindow()
    {
        var spans = new[]
        {
            Ended(Utc(9, 10), Utc(9, 11)),
            Ended(Utc(14, 23), Utc(15, 1)),
            // Started before the window and ran into it: its hours count, it was not held here.
            Ended(Utc(7, 23), Utc(8, 1)),
            // Starts exactly on `to`: exclusive.
            Ended(Utc(15), Utc(15, 1)),
        };

        var totals = AdminMeetingInsightsCalculator.Totals(spans, From, To, Now);

        Assert.Equal(2, totals.MeetingsHeld);
    }

    [Fact]
    public void Hours_AreClippedToTheWindow()
    {
        var spans = new[]
        {
            Ended(Utc(9, 10), Utc(9, 11, 30)),   // 1.5 h inside
            Ended(Utc(14, 23), Utc(15, 1)),      // 1 h inside, 1 h after `to`
            Ended(Utc(7, 23), Utc(8, 1)),        // 1 h before `from`, 1 h inside
            Ended(Utc(1), Utc(2)),               // entirely before
        };

        var totals = AdminMeetingInsightsCalculator.Totals(spans, From, To, Now);

        Assert.Equal(3.5m, totals.Hours);
    }

    [Fact]
    public void Hours_SplitAMeetingAcrossMidnightBetweenItsDays_AndDaysSumToTheTotal()
    {
        var spans = new[] { Ended(Utc(9, 22), Utc(10, 2)), Ended(Utc(10, 9), Utc(10, 9, 45)) };

        var days = AdminMeetingInsightsCalculator.ByDay(spans, From, To, Now);
        var totals = AdminMeetingInsightsCalculator.Totals(spans, From, To, Now);

        Assert.Equal(7, days.Count);
        Assert.Equal("2026-09-08", days[0].Date);
        var ninth = days.Single(d => d.Date == "2026-09-09");
        var tenth = days.Single(d => d.Date == "2026-09-10");
        Assert.Equal((1, 2m), (ninth.Meetings, ninth.Hours));
        Assert.Equal((1, 2.75m), (tenth.Meetings, tenth.Hours));
        Assert.Equal(0, days.Single(d => d.Date == "2026-09-11").Meetings);
        Assert.Equal(totals.Hours, days.Sum(d => d.Hours));
    }

    [Fact]
    public void ByDay_ClipsAPartialFirstDayToTheWindow()
    {
        var spans = new[] { Ended(Utc(8, 1), Utc(8, 5)) };

        var days = AdminMeetingInsightsCalculator.ByDay(spans, Utc(8, 3), Utc(9), Now);

        var only = Assert.Single(days);
        Assert.Equal(0, only.Meetings);
        Assert.Equal(2m, only.Hours);
    }

    [Fact]
    public void LiveMeeting_RunsToNow()
    {
        var spans = new[] { new AdminMeetingSpan(Utc(17, 8), null, null, "IN_PROGRESS") };

        var totals = AdminMeetingInsightsCalculator.Totals(spans, Utc(17), Utc(18), now: Utc(17, 10, 30));

        Assert.Equal(1, totals.MeetingsHeld);
        Assert.Equal(2.5m, totals.Hours);
        Assert.Equal(0, totals.CappedLive);
    }

    [Fact]
    public void StaleLiveMeeting_IsCappedAt24Hours_AndSaysSo()
    {
        var spans = new[] { new AdminMeetingSpan(Utc(1, 12), null, null, "PAUSED") };

        var current = AdminMeetingInsightsCalculator.Totals(spans, Utc(1), Utc(8), Now);
        var later = AdminMeetingInsightsCalculator.Totals(spans, From, To, Now);

        Assert.Equal(24m, current.Hours);
        Assert.Equal(1, current.CappedLive);
        // Nothing of it is left to count a week later.
        Assert.Equal(0m, later.Hours);
        Assert.Equal(0, later.CappedLive);
        Assert.Contains("24 h", AdminMeetingInsightsCalculator.HoursNote(current, later));
    }

    [Fact]
    public void EndedWithoutEndTime_UsesDurationSeconds_WhenRecorded()
    {
        var spans = new[] { new AdminMeetingSpan(Utc(9, 10), null, 5400, "ENDED") };

        var totals = AdminMeetingInsightsCalculator.Totals(spans, From, To, Now);

        Assert.Equal(1.5m, totals.Hours);
        Assert.Equal(0, totals.ExcludedNoEnd);
    }

    [Fact]
    public void EndedWithNoEndAndNoDuration_IsHeldButExcludedFromHours_AndSaysSo()
    {
        var spans = new[] { new AdminMeetingSpan(Utc(9, 10), null, null, "CANCELLED") };

        var current = AdminMeetingInsightsCalculator.Totals(spans, From, To, Now);
        var previous = AdminMeetingInsightsCalculator.Totals([], Utc(1), From, Now);

        Assert.Equal(1, current.MeetingsHeld);
        Assert.Equal(0m, current.Hours);
        Assert.Equal(1, current.ExcludedNoEnd);
        Assert.Contains("no recorded end time (1 this period, 0 previous)", AdminMeetingInsightsCalculator.HoursNote(current, previous));
    }

    [Fact]
    public void HoursNote_IsNullWhenEverythingWasCounted()
    {
        var totals = AdminMeetingInsightsCalculator.Totals([Ended(Utc(9, 10), Utc(9, 11))], From, To, Now);

        Assert.Null(AdminMeetingInsightsCalculator.HoursNote(totals, totals));
    }
}
