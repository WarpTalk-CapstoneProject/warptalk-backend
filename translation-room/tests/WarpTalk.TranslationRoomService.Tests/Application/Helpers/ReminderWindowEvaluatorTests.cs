using System;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.Helpers;
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
}
