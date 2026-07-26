namespace WarpTalk.BillingService.Infrastructure.Helpers;

public static class InvoiceReminderHelper
{
    public const string SevenDaysBeforeDue = "T-7d";
    public const string OneDayBeforeDue = "T-1d";
    public const string SevenDaysOverdue = "T+7d";

    public static string? ResolveReminderKind(DateTime dueAt, DateTime now)
    {
        if (dueAt <= now.AddDays(-7))
            return SevenDaysOverdue;
        if (dueAt <= now)
            return null;
        if (dueAt <= now.AddDays(1))
            return OneDayBeforeDue;
        if (dueAt <= now.AddDays(7))
            return SevenDaysBeforeDue;
        return null;
    }

    public static string DescribeReminder(string kind) => kind switch
    {
        SevenDaysBeforeDue => "due within 7 days",
        OneDayBeforeDue => "due within 1 day",
        SevenDaysOverdue => "7 days overdue",
        _ => "due soon"
    };
}
