namespace WarpTalk.NotificationService.Domain.Entities;

/// <summary>
/// How many of one email in one locale were handed to a provider on one UTC day, and how many the
/// provider refused. There was no send log to count from, so senders report each send
/// (RecordEmailDelivery) and this is the counter it increments.
/// </summary>
public partial class EmailDeliveryStat
{
    public string TemplateKey { get; set; } = null!;

    public string Locale { get; set; } = null!;

    public DateOnly Day { get; set; }

    public int SentCount { get; set; }

    public int FailedCount { get; set; }
}
