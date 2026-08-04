using FluentAssertions;
using WarpTalk.BillingService.Infrastructure.Helpers;

namespace WarpTalk.BillingService.Tests.Infrastructure.Helpers;

public class InvoiceReminderHelperTests
{
    [Fact]
    public void ResolveReminderKind_Should_Return_TMinus7_For_Due_Within_Seven_Days()
    {
        var now = new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc);

        InvoiceReminderHelper.ResolveReminderKind(now.AddDays(7), now)
            .Should()
            .Be(InvoiceReminderHelper.SevenDaysBeforeDue);
    }

    [Fact]
    public void ResolveReminderKind_Should_Return_TMinus1_For_Due_Within_One_Day()
    {
        var now = new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc);

        InvoiceReminderHelper.ResolveReminderKind(now.AddDays(1), now)
            .Should()
            .Be(InvoiceReminderHelper.OneDayBeforeDue);
    }

    [Fact]
    public void ResolveReminderKind_Should_Return_TPlus7_For_Seven_Days_Overdue()
    {
        var now = new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc);

        InvoiceReminderHelper.ResolveReminderKind(now.AddDays(-7), now)
            .Should()
            .Be(InvoiceReminderHelper.SevenDaysOverdue);
    }

    [Fact]
    public void ResolveReminderKind_Should_Not_Send_Reminder_On_Due_Date()
    {
        var now = new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc);

        InvoiceReminderHelper.ResolveReminderKind(now, now).Should().BeNull();
    }
}
