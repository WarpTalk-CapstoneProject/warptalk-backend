using System;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Domain.Entities;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

public class ReminderWindowEvaluatorTests
{
    private static readonly DateTime ScheduledAt = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ShouldSendReminder_ReturnsTrue_WhenInsideTenMinuteWindow_AndNotYetSent()
    {
        var now = ScheduledAt.AddMinutes(-9); // 9 minutes before start, inside the 10-min window

        var result = ReminderWindowEvaluator.ShouldSendReminder(ScheduledAt, now, alreadySentAtUtc: null, ReminderWindowEvaluator.TenMinuteWindow);

        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldSendReminder_ReturnsTrue_AtExactWindowStart()
    {
        var now = ScheduledAt - ReminderWindowEvaluator.TenMinuteWindow; // exactly T-10min

        var result = ReminderWindowEvaluator.ShouldSendReminder(ScheduledAt, now, alreadySentAtUtc: null, ReminderWindowEvaluator.TenMinuteWindow);

        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldSendReminder_ReturnsFalse_BeforeWindowStart()
    {
        var now = ScheduledAt.AddMinutes(-11); // still 11 minutes out, before the 10-min window opens

        var result = ReminderWindowEvaluator.ShouldSendReminder(ScheduledAt, now, alreadySentAtUtc: null, ReminderWindowEvaluator.TenMinuteWindow);

        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldSendReminder_ReturnsFalse_AtOrAfterScheduledStart()
    {
        var atStart = ScheduledAt;
        var afterStart = ScheduledAt.AddMinutes(1);

        ReminderWindowEvaluator.ShouldSendReminder(ScheduledAt, atStart, null, ReminderWindowEvaluator.TenMinuteWindow).Should().BeFalse();
        ReminderWindowEvaluator.ShouldSendReminder(ScheduledAt, afterStart, null, ReminderWindowEvaluator.TenMinuteWindow).Should().BeFalse();
    }

    [Fact]
    public void ShouldSendReminder_ReturnsFalse_WhenAlreadySentForThisWindow()
    {
        var now = ScheduledAt.AddMinutes(-5);
        var alreadySentAt = ScheduledAt.AddMinutes(-9);

        var result = ReminderWindowEvaluator.ShouldSendReminder(ScheduledAt, now, alreadySentAt, ReminderWindowEvaluator.TenMinuteWindow);

        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldSendReminder_TenAndOneMinuteWindows_AreIndependent()
    {
        // A room already reminded for T-10min should still be eligible for T-1min once it enters that window.
        var tenMinAlreadySentAt = ScheduledAt.AddMinutes(-10);
        var nowAtOneMinuteWindow = ScheduledAt.AddMinutes(-1);

        var tenMinResult = ReminderWindowEvaluator.ShouldSendReminder(ScheduledAt, nowAtOneMinuteWindow, tenMinAlreadySentAt, ReminderWindowEvaluator.TenMinuteWindow);
        var oneMinResult = ReminderWindowEvaluator.ShouldSendReminder(ScheduledAt, nowAtOneMinuteWindow, null, ReminderWindowEvaluator.OneMinuteWindow);

        tenMinResult.Should().BeFalse("the T-10min reminder was already sent");
        oneMinResult.Should().BeTrue("the room just entered the T-1min window and hasn't been reminded for it yet");
    }

    [Fact]
    public void ShouldSendReminder_ReturnsFalse_WhenScheduledTimeIsInThePast_AndNeverSent()
    {
        // Guards against a worker that was down across the whole window: once the meeting's
        // start time itself has passed, firing a "starting in N minutes" reminder no longer
        // makes sense — see the nowUtc < scheduledAtUtc bound.
        var now = ScheduledAt.AddHours(2);

        var result = ReminderWindowEvaluator.ShouldSendReminder(ScheduledAt, now, alreadySentAtUtc: null, ReminderWindowEvaluator.TenMinuteWindow);

        result.Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────
    // WT-326: the third window, T-30min
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void ThirtyMinuteWindow_IsThirtyMinutes_AndIsTheWidest()
    {
        ReminderWindowEvaluator.ThirtyMinuteWindow.Should().Be(TimeSpan.FromMinutes(30));
        ReminderWindowEvaluator.WidestWindow.Should().Be(ReminderWindowEvaluator.ThirtyMinuteWindow,
            "SweepCandidateFilter's range bound is derived from WidestWindow — if it lags behind the "
            + "widest window, rooms silently stop being swept in time for it");
    }

    [Fact]
    public void ShouldSendReminder_ReturnsTrue_InsideTheThirtyMinuteWindow()
    {
        var now = ScheduledAt.AddMinutes(-25);

        ReminderWindowEvaluator.ShouldSendReminder(ScheduledAt, now, null, ReminderWindowEvaluator.ThirtyMinuteWindow)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldSendReminder_ReturnsTrue_AtExactlyThirtyMinutesBefore()
    {
        var now = ScheduledAt - ReminderWindowEvaluator.ThirtyMinuteWindow;

        ReminderWindowEvaluator.ShouldSendReminder(ScheduledAt, now, null, ReminderWindowEvaluator.ThirtyMinuteWindow)
            .Should().BeTrue("the window is inclusive at its start, same as the other two");
    }

    [Fact]
    public void ShouldSendReminder_ReturnsFalse_OneSecondBeforeTheThirtyMinuteWindowOpens()
    {
        var now = ScheduledAt - ReminderWindowEvaluator.ThirtyMinuteWindow - TimeSpan.FromSeconds(1);

        ReminderWindowEvaluator.ShouldSendReminder(ScheduledAt, now, null, ReminderWindowEvaluator.ThirtyMinuteWindow)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldSendReminder_ReturnsFalse_ForTheThirtyMinuteWindowAtOrAfterStart()
    {
        ReminderWindowEvaluator.ShouldSendReminder(ScheduledAt, ScheduledAt, null, ReminderWindowEvaluator.ThirtyMinuteWindow)
            .Should().BeFalse();
        ReminderWindowEvaluator.ShouldSendReminder(ScheduledAt, ScheduledAt.AddMinutes(20), null, ReminderWindowEvaluator.ThirtyMinuteWindow)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldSendReminder_ReturnsFalse_WhenTheThirtyMinuteWindowWasAlreadySent()
    {
        var alreadySentAt = ScheduledAt.AddMinutes(-30);

        ReminderWindowEvaluator.ShouldSendReminder(ScheduledAt, ScheduledAt.AddMinutes(-20), alreadySentAt, ReminderWindowEvaluator.ThirtyMinuteWindow)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldSendReminder_AllThreeWindows_AreIndependent()
    {
        // At T-1min a room reminded at T-30min and T-10min is still owed its T-1min reminder, and
        // neither of the wider windows may fire a second time.
        var now = ScheduledAt.AddMinutes(-1);

        ReminderWindowEvaluator.ShouldSendReminder(ScheduledAt, now, ScheduledAt.AddMinutes(-30), ReminderWindowEvaluator.ThirtyMinuteWindow)
            .Should().BeFalse();
        ReminderWindowEvaluator.ShouldSendReminder(ScheduledAt, now, ScheduledAt.AddMinutes(-10), ReminderWindowEvaluator.TenMinuteWindow)
            .Should().BeFalse();
        ReminderWindowEvaluator.ShouldSendReminder(ScheduledAt, now, null, ReminderWindowEvaluator.OneMinuteWindow)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldSendReminder_NarrowerWindowsAreNotYetOpen_AtTheStartOfTheThirtyMinuteOne()
    {
        var now = ScheduledAt.AddMinutes(-30);

        ReminderWindowEvaluator.ShouldSendReminder(ScheduledAt, now, null, ReminderWindowEvaluator.ThirtyMinuteWindow)
            .Should().BeTrue();
        ReminderWindowEvaluator.ShouldSendReminder(ScheduledAt, now, null, ReminderWindowEvaluator.TenMinuteWindow)
            .Should().BeFalse();
        ReminderWindowEvaluator.ShouldSendReminder(ScheduledAt, now, null, ReminderWindowEvaluator.OneMinuteWindow)
            .Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────
    // WT-326: the SQL-side prefilter must not exclude anything ShouldSendReminder would fire for
    // ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("SCHEDULED", true)]
    [InlineData("WAITING", true)]
    [InlineData("IN_PROGRESS", false)]
    [InlineData("PAUSED", false)]
    [InlineData("CANCELLED", false)]
    [InlineData("ENDED", false)]
    [InlineData("EXPIRED", false)]
    [InlineData("FAILED", false)]
    public void SweepCandidateFilter_SelectsOnlyRoomsThatHaveNotStarted(string status, bool expected)
    {
        // WAITING is the WT-326 case: OpenWaitingRoomAsync flips SCHEDULED -> WAITING with no
        // time gate, so a host who opened the lobby early used to fall out of the sweep forever.
        var now = ScheduledAt.AddMinutes(-5);

        Matches(Room(status, ScheduledAt), now).Should().Be(expected);
    }

    [Fact]
    public void SweepCandidateFilter_IncludesTheExactStartOfTheWidestWindow()
    {
        var now = ScheduledAt - ReminderWindowEvaluator.WidestWindow;

        Matches(Room("SCHEDULED", ScheduledAt), now).Should().BeTrue();
    }

    [Fact]
    public void SweepCandidateFilter_ExcludesRoomsStillOutsideTheWidestWindow()
    {
        var now = ScheduledAt - ReminderWindowEvaluator.WidestWindow - TimeSpan.FromSeconds(1);

        Matches(Room("SCHEDULED", ScheduledAt), now).Should().BeFalse();
    }

    [Fact]
    public void SweepCandidateFilter_ExcludesRoomsThatHaveAlreadyStarted()
    {
        // Same bound as ShouldSendReminder's `nowUtc < scheduledAtUtc`, so a room that was never
        // stamped — worker down across its window, or a recipient that never recovered — leaves
        // the sweep by itself instead of accumulating.
        Matches(Room("SCHEDULED", ScheduledAt), ScheduledAt).Should().BeFalse();
        Matches(Room("WAITING", ScheduledAt), ScheduledAt.AddHours(1)).Should().BeFalse();
    }

    [Fact]
    public void SweepCandidateFilter_ExcludesRoomsWithNoScheduledTime()
    {
        Matches(Room("SCHEDULED", scheduledAt: null), ScheduledAt.AddMinutes(-5)).Should().BeFalse();
    }

    [Fact]
    public void SweepCandidateFilter_ExcludesRoomsWithEveryWindowAlreadyStamped()
    {
        var room = Room("WAITING", ScheduledAt);
        room.Reminder30MinSentAt = ScheduledAt.AddMinutes(-30);
        room.Reminder10MinSentAt = ScheduledAt.AddMinutes(-10);
        room.Reminder1MinSentAt = ScheduledAt.AddMinutes(-1);

        Matches(room, ScheduledAt.AddMinutes(-5)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void SweepCandidateFilter_KeepsRoomsWithAnyWindowStillUnstamped(bool thirty, bool ten, bool one)
    {
        var room = Room("WAITING", ScheduledAt);
        if (thirty) room.Reminder30MinSentAt = ScheduledAt.AddMinutes(-30);
        if (ten) room.Reminder10MinSentAt = ScheduledAt.AddMinutes(-10);
        if (one) room.Reminder1MinSentAt = ScheduledAt.AddMinutes(-1);

        Matches(room, ScheduledAt.AddMinutes(-5)).Should().BeTrue();
    }

    private static bool Matches(TranslationRoom room, DateTime nowUtc)
        => ReminderWindowEvaluator.SweepCandidateFilter(nowUtc).Compile()(room);

    private static TranslationRoom Room(string status, DateTime? scheduledAt) => new()
    {
        Id = Guid.NewGuid(),
        HostId = Guid.NewGuid(),
        Title = "Sprint review",
        TranslationRoomCode = "ABC-123",
        Status = status,
        TranslationRoomType = "SCHEDULED",
        SourceLanguage = "en",
        TargetLanguages = "[\"vi\"]",
        Settings = "{}",
        ScheduledAt = scheduledAt,
    };
}
