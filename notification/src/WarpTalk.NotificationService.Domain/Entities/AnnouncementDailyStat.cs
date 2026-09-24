namespace WarpTalk.NotificationService.Domain.Entities;

/// <summary>Per-day counters for one announcement, for the analytics chart.</summary>
public partial class AnnouncementDailyStat
{
    public Guid AnnouncementId { get; set; }

    public DateOnly Day { get; set; }

    public int Impressions { get; set; }

    public int Dismissals { get; set; }

    public int CtaClicks { get; set; }

    public int SecondaryClicks { get; set; }
}
